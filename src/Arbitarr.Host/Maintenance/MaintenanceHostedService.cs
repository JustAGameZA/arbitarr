using Arbitarr.Data;
using Arbitarr.Data.Logging;
using Arbitarr.Data.Maintenance;
using Arbitarr.Data.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Host.Maintenance;

/// <summary>
/// M7-3a: schedules <see cref="MaintenanceJob"/> to run on the <c>maintenance_job_interval</c>
/// setting. Per <see cref="Arbitarr.Core.Settings.SettingsValidator.ValidateMaintenanceJobInterval"/>, this is the one
/// setting explicitly permitted to require a restart to take effect: the interval is read once at
/// startup (the first loop iteration) and reused for every subsequent delay rather than being
/// re-resolved every cycle the way <c>RefreshWorker</c>'s tunables are (M7-8b/AC24 does not apply
/// here by design). A changed interval only takes effect after the host restarts.
///
/// A fresh DI scope is opened for every run so the job gets its own <see cref="ArbitarrDbContext"/>
/// (scoped) rather than one held open for the process lifetime, matching <c>RefreshWorker</c>'s
/// per-cycle scoping pattern.
///
/// #65 added a SECOND job to each pass: trimming the separate application-log database. It rides
/// this existing timer rather than getting a timer of its own because it is the same kind of work
/// on the same cadence, and a second scheduler would be a second thing to reason about when the
/// disk fills. It runs in its own try/catch so a failure in either job cannot stop the other —
/// notably, a log-store failure must not prevent the main database from being pruned.
///
/// #56 added a THIRD, on the same reasoning and with the same isolation: the automatic
/// configuration backup and its bounded retention (<see cref="Arbitarr.Data.Backup.AutomaticBackupJob"/>).
/// Its own try/catch matters more here than for the other two, because it is the only one that can
/// fail for a reason outside this process's control — a full config volume — and a box that cannot
/// write a backup must still prune its database.
///
/// arb-x7w8.5 added a FOURTH, again on the same reasoning and again isolated: re-fetching every
/// enabled source's caps into the caps cache (<see cref="RefreshSourceCapsAsync"/>). It is the only
/// pass here that reaches the network, so its own try/catch is what keeps a box with unreachable
/// indexers pruning its database normally.
///
/// Every pass therefore touches all THREE stores under the config directory in a deliberate order:
/// arbitarr.db (prune + vacuum), the separate log database named by
/// <see cref="LogStore.DatabaseFileName"/> (trim), and the backup directory (take + prune). The two
/// SQLite files are separate on purpose; anything added here that spans "the databases" must grep
/// for that constant rather than for "arbitarr.db".
/// </summary>
public sealed class MaintenanceHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<MaintenanceHostedService>? logger = null)
    : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<MaintenanceHostedService>.Instance;

    private readonly TaskCompletionSource _firstPassCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Set by <see cref="RunIterationAsync"/> in place of the <c>break</c> it used to execute when
    /// a step observed cancellation, and read by <see cref="RunLoopAsync"/> immediately afterwards.
    /// Not volatile and not interlocked on purpose: it is written and read on the SINGLE
    /// <see cref="ExecuteAsync"/> task, never from another thread, with an await between the two
    /// only in the sense that the awaits happen before the write.
    /// </summary>
    private bool _stopRequested;

    /// <summary>
    /// arb-km0a: completes once this service's FIRST pass has finished, whatever its outcome —
    /// every step succeeded, a step threw and was absorbed by its own catch, or the host was
    /// stopped part-way through. A test host awaits this before reading state the first pass
    /// writes, instead of racing it.
    ///
    /// <para><b>Why the seam is needed at all.</b> <c>BackgroundService.StartAsync</c> returns at
    /// <see cref="ExecuteAsync"/>'s FIRST await (<see cref="ResolveIntervalAsync"/>, a settings
    /// read), so "the host has started" says nothing about the first pass having run. The automatic
    /// backup step is deliberately immediate — it runs before the first <c>Task.Delay</c>, see the
    /// arb-rwhb remarks below — so a startup backup failure it records into
    /// <c>BackupStateStore</c> can land in the middle of an unrelated test body. That is the
    /// three-times-observed defect arb-km0a fixes.</para>
    ///
    /// <para><b>Completed from a <c>finally</c>, exactly like
    /// <c>SqliteLoggerProvider.DrainCompleted</c>, and for the same reason.</b> A waiter must never
    /// be stranded by the one case the seam exists to survive — the first pass not finishing the
    /// way it planned to. So it publishes even when the loop breaks on cancellation and even if
    /// something outside the per-step catches throws. This task therefore never faults and never
    /// cancels; awaiting it cannot throw. It is completed exactly once (the <c>finally</c> is
    /// inside the first iteration only), and <see cref="TaskCompletionSource.TrySetResult"/> keeps
    /// that safe regardless.</para>
    ///
    /// <para>This is a completion signal, NOT a success signal: a caller that needs to know whether
    /// the pass succeeded reads the state the pass writes (<c>BackupStateStore.LastBackupFailure</c>),
    /// which is the whole point of awaiting this first.</para>
    /// </summary>
    public Task FirstPassCompleted => _firstPassCompleted.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The interval is resolved once, from the first scope, and reused for the lifetime of
            // the service -- see the restart-required rationale above.
            var interval = await ResolveIntervalAsync(stoppingToken).ConfigureAwait(false);

            await RunLoopAsync(interval, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // arb-km0a: the outermost finally covers the paths RunLoopAsync's own publication
            // cannot -- ResolveIntervalAsync throwing or being cancelled before the first iteration
            // is ever entered. A host that never reaches a pass at all must still release its
            // awaiters rather than leave them hanging until their bound elapses.
            _firstPassCompleted.TrySetResult();
        }
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        // arb-km0a: false for the first iteration only; set once it has published, so the
        // per-iteration finally below is a no-op for every subsequent cycle.
        var firstPass = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                // arb-km0a: published here, AFTER the pass's work steps and BEFORE the delay below.
                // Putting it after the delay instead would make the "completion" arrive one whole
                // maintenance interval late -- and under a FakeTimeProvider, which never advances on
                // its own, never at all. The seam's meaning is "the work this pass does is done",
                // which is precisely what a caller reading the state that work writes needs.
                if (firstPass)
                {
                    firstPass = false;
                    _firstPassCompleted.TrySetResult();
                }
            }

            if (_stopRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// arb-km0a: the WORK of one pass — the four steps, in order, each with the separate catch it
    /// had. Extracted from <see cref="ExecuteAsync"/>'s loop body so the first iteration has a seam
    /// to publish <see cref="FirstPassCompleted"/> from.
    ///
    /// <para>Two mechanical changes, neither of them behavioural. The <c>break</c>s the loop body
    /// used became <see cref="_stopRequested"/>, because a <c>break</c> cannot cross a method
    /// boundary; <see cref="RunLoopAsync"/> breaks on that flag immediately after this returns, so
    /// the loop still exits at the same point. And the INTER-CYCLE DELAY STAYED IN THE LOOP rather
    /// than coming along — it is the wait BETWEEN passes, not part of one, and it must sit on the
    /// far side of the publication or the completion would arrive an interval late. The steps
    /// themselves, their order, their isolation from one another and the immediacy of the first
    /// pass are all exactly as they were.</para>
    /// </summary>
    private async Task RunIterationAsync(CancellationToken stoppingToken)
    {
        // The bare block preserves the loop body's original INDENTATION, so this extraction shows
        // up in the diff as the four break-to-return rewrites and nothing else. Re-indenting a
        // hundred lines of load-bearing commentary to save one brace would bury those four lines in
        // whitespace churn and make the next reviewer of this method diff it against the wrong
        // thing.
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _stopRequested = true;
                return;
            }
            catch (Exception ex)
            {
                // Per-item error handling (docs/standards/architecture.md). Log and retry on the
                // next tick.
                _logger.LogError(ex, "Maintenance job run failed; will retry next cycle.");
            }

            try
            {
                await TrimLogDatabaseAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _stopRequested = true;
                return;
            }
            catch (Exception ex)
            {
                // Separate catch from the main-database pass above on purpose: the two jobs touch
                // different SQLite files, and a failure on the log store (locked file, no space)
                // must not stop the main database from being pruned. Same next-cycle retry.
                _logger.LogError(ex, "Log database trim failed; will retry next cycle.");
            }

            try
            {
                await RunAutomaticBackupAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // arb-rwhb: this arm is what makes a shutdown mid-backup CLEAN rather than an
                // error. BackupService.WriteArchiveAsync now checks the token immediately before
                // SqliteConnection.BackupDatabase — a blocking call that cannot be cancelled once
                // entered — so a stop signalled while this service is between cycles declines the
                // copy instead of starting one that shutdown would then pull the files out from
                // under. Declining that way lands HERE, not in the catch below: a backup skipped
                // because the host is stopping is not a backup FAILURE, so it must neither log an
                // error nor call RecordBackupFailure and show a broken safety net in the UI.
                //
                // The first pass is still deliberately IMMEDIATE (it runs before the first
                // Task.Delay below): operators rely on a backup being taken at startup, so the fix
                // for the shutdown race is the token check, never deferring that first pass.
                //
                // NOTHING HERE IS DETACHED, and that is load-bearing rather than incidental. Every
                // pass above is awaited inline on ExecuteAsync's own task — there is no Task.Run and
                // no async void anywhere in this service or in the backup chain beneath it — so the
                // task BackgroundService.StopAsync awaits IS the one running the backup, and a stop
                // therefore waits for an in-flight copy to finish (bounded by the host's shutdown
                // timeout, the existing contract) instead of abandoning it. Introducing a
                // fire-and-forget dispatch in this chain would silently reopen arb-rwhb: the copy
                // would outlive StopAsync and read files the config directory's owner then deletes.
                _stopRequested = true;
                return;
            }
            catch (Exception ex)
            {
                // Third separate catch, same rule as the two above: a config volume with no space
                // left fails HERE first (a backup is the largest single write this process makes),
                // and that must not stop the pruning that might free the space.
                //
                // The failure is also RECORDED, not only logged. Logging alone leaves the Backup
                // tab showing the last SUCCESSFUL backup's timestamp with nothing to say the safety
                // net has been broken since — a stale backup that reads as a fresh one, which is
                // the exact failure #56 exists to prevent. Only the type and message are kept: this
                // is rendered in the UI, and the stack would carry config-directory paths.
                _logger.LogError(ex, "Automatic configuration backup failed; will retry next cycle.");
                RecordBackupFailure(ex);
            }

            try
            {
                await RefreshSourceCapsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _stopRequested = true;
                return;
            }
            catch (Exception ex)
            {
                // Fourth separate catch, same rule as the three above. This is the only pass that
                // reaches the NETWORK, so it is the one most likely to fail on a box whose indexers
                // are down — and pruning the database must not be hostage to that.
                // CapsRefresher already absorbs a per-source fetch failure without writing, so
                // anything landing here is a failure to resolve the source SET at all.
                _logger.LogError(ex, "Source caps refresh failed; will retry next cycle.");
            }
        }
    }

    private async Task<TimeSpan> ResolveIntervalAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsRepository = scope.ServiceProvider.GetRequiredService<SettingsRepository>();
        var snapshot = await settingsRepository.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.MaintenanceJobInterval;
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        var settingsRepository = provider.GetRequiredService<SettingsRepository>();
        var snapshot = await settingsRepository.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);

        var dbContext = provider.GetRequiredService<ArbitarrDbContext>();
        var job = new MaintenanceJob(dbContext, timeProvider);
        await job.RunAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// #65: prunes the application-log database to <see cref="LogRetentionPolicy.Retention"/> and
    /// vacuums it. <see cref="LogStore.TrimAsync"/> does both — the vacuum is the half that
    /// actually returns disk space, and the half without which this whole method is decorative;
    /// see <see cref="LogRetentionPolicy"/>.
    ///
    /// Resolved via <c>GetService</c> rather than <c>GetRequiredService</c>: <see cref="LogStore"/>
    /// is registered by the Host composition root, and several test hosts build a narrower service
    /// collection without it. Maintenance of the main database must not fail merely because a test
    /// host has no log store.
    /// </summary>
    private async Task TrimLogDatabaseAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetService<LogStore>();
        if (store is null)
        {
            return;
        }

        await store.TrimAsync(timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// #56: takes one automatic configuration backup and prunes older archives to the retained
    /// count. A count of 0 disables the feature and this does nothing.
    ///
    /// Resolved via <c>GetService</c> for the same reason <see cref="TrimLogDatabaseAsync"/> is:
    /// several test hosts build a narrower service collection without the backup registrations, and
    /// maintenance of the main database must not fail merely because one of them has no backup
    /// paths configured.
    /// </summary>
    private async Task RunAutomaticBackupAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        var job = provider.GetService<Arbitarr.Data.Backup.AutomaticBackupJob>();
        var reader = provider.GetService<SettingsReader>();
        if (job is null || reader is null)
        {
            return;
        }

        var retainedCount = await reader.GetAutomaticBackupRetainedCountAsync(cancellationToken)
            .ConfigureAwait(false);

        await job.RunAsync(retainedCount, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// arb-x7w8.5: re-fetches every enabled source's caps into <see cref="Arbitarr.Core.Sources.ICapsCacheStore"/>
    /// on this same cadence, so the stored value the search and caps paths serve keeps up with an
    /// upstream that added a category — without any search ever making a live caps call.
    ///
    /// <para><b>It rides this timer rather than getting one of its own</b>, for the same reason the
    /// log trim and the backup do: caps change on the order of MONTHLY (which is the whole premise of
    /// storing them), so any cadence a maintenance pass runs at is far more frequent than the value
    /// changes, and a second scheduler would be a second thing to reason about for no gain.</para>
    ///
    /// <para><b>THE LAST-KNOWN-GOOD ENTRY IS NEVER BLANKED BY A FAILED PASS.</b>
    /// <see cref="Arbitarr.Core.Sources.CapsRefresher"/> writes only on a successful fetch, so a run
    /// against a down indexer leaves the previous entry in place — a background pass that overwrote
    /// the store with an empty result would defeat the aggregator's fallback on exactly the schedule
    /// designed to keep it fresh.</para>
    ///
    /// <para>Resolved with <c>GetService</c> and skipped when absent, for the same reason the log trim
    /// and the backup are: several narrower test hosts register no source registry, and maintenance of
    /// the main database must not fail merely because one of them cannot resolve an indexer.</para>
    /// </summary>
    private async Task RefreshSourceCapsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        var registry = provider.GetService<Arbitarr.Core.Sources.ISourceRegistry>();
        var refresher = provider.GetService<Arbitarr.Core.Sources.CapsRefresher>();
        if (registry is null || refresher is null)
        {
            return;
        }

        var sources = await registry.ResolveAsync(cancellationToken).ConfigureAwait(false);
        await refresher.RefreshAllAsync(sources, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a failed automatic-backup pass on <see cref="Arbitarr.Data.Backup.BackupStateStore"/> so
    /// <c>GET /api/admin/backup/status</c> can explain a timestamp that stopped advancing.
    ///
    /// Resolved with <c>GetService</c> and silently skipped when absent, for the same reason the
    /// backup itself is: several narrower test hosts register no backup services, and recording
    /// provenance must never be the thing that breaks a maintenance pass.
    /// </summary>
    private void RecordBackupFailure(Exception ex)
    {
        using var scope = scopeFactory.CreateScope();
        var state = scope.ServiceProvider.GetService<Arbitarr.Data.Backup.BackupStateStore>();
        state?.RecordBackupFailure(
            timeProvider.GetUtcNow(),
            ex.GetType().Name + ": " + ex.Message);
    }
}
