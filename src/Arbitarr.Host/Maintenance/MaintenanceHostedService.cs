using Arbitarr.Data;
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
}
