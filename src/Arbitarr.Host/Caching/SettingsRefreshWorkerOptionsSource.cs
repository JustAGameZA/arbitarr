using Arbitarr.Core.Caching;
using Arbitarr.Data.Settings;

namespace Arbitarr.Host.Caching;

/// <summary>
/// Host-side <see cref="IRefreshWorkerOptionsSource"/> implementation (M7-8b/AC24): reads the live
/// <see cref="Arbitarr.Core.Settings.SettingsSnapshot"/> via <see cref="SettingsRepository"/> on every
/// call, so a setting changed through the admin API takes effect on the worker's very next cycle
/// without a restart. Registered scoped in <c>Program.cs</c> — <see cref="RefreshWorker"/>'s
/// scope-factory constructor resolves a fresh instance (backed by a fresh <c>ArbitarrDbContext</c>)
/// from a new DI scope on every cycle.
///
/// <see cref="RefreshWorkerOptions.RepopulationSpreadWindow"/> and
/// <see cref="RefreshWorkerOptions.MaxConcurrentRefreshes"/> have no corresponding
/// <c>SettingsSnapshot</c> field (they are not in the persisted settings catalog), so they remain
/// derived/constant exactly as <see cref="RefreshWorkerDefaults"/> defines them.
/// </summary>
public sealed class SettingsRefreshWorkerOptionsSource(SettingsRepository settingsRepository) : IRefreshWorkerOptionsSource
{
    public async ValueTask<RefreshWorkerOptions> GetAsync(CancellationToken cancellationToken)
    {
        var snapshot = await settingsRepository.LoadSnapshotAsync(cancellationToken);

        return new RefreshWorkerOptions(
            snapshot.WorkerEnabled,
            snapshot.WorkerCycleInterval,
            snapshot.ActiveWindow,
            snapshot.RefreshLead,
            snapshot.FreshUntil,
            snapshot.ServeUntil,
            RefreshWorkerDefaults.RepopulationSpreadWindow,
            RefreshWorkerDefaults.MaxConcurrentRefreshes);
    }
}
