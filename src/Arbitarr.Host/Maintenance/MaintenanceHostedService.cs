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
/// setting. Per <see cref="SettingsValidator.ValidateMaintenanceJobInterval"/>, this is the one
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
                // A failed maintenance pass must not take the host down: BackgroundService faults
                // propagate to the host by default. Log and retry on the next tick.
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
}
