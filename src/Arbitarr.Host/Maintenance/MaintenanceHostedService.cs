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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The interval is resolved once, from the first scope, and reused for the lifetime of the
        // service -- see the restart-required rationale above.
        var interval = await ResolveIntervalAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
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
                break;
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
                break;
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
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
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
