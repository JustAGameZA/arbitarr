using Arbitarr.Core.Settings;

namespace Arbitarr.Core.Caching;

/// <summary>
/// Shared, unconfigured defaults for the two-age cache's fresh/serve-until ages, sourced from
/// <see cref="SettingsSnapshot.Defaults"/> so the request-path cache stage (<see
/// cref="Arbitarr.Api.Search.SearchResultCacheStage"/>, referenced here only by doc comment to avoid
/// a Core→Api reference) and the Host DI-wired <see cref="RefreshWorkerOptions"/> agree on the same
/// values without duplicating the magic numbers. No settings-store/repository exists yet (M3 scope
/// per team-lead), so this is a fixed snapshot rather than a live-reloadable value; wiring it to a
/// persisted settings store is out of scope for Step 4a.
/// </summary>
public static class RefreshWorkerDefaults
{
    // Arbitrary placeholder *arr RSS sync interval used only to derive ActiveWindow's default (24x
    // this value, per SettingsSnapshot.Defaults); the AC0c-measured real value belongs to a future
    // settings-store integration, not this cache-stage wiring.
    private static readonly TimeSpan PlaceholderArrSyncInterval = TimeSpan.FromMinutes(15);

    private static readonly SettingsSnapshot Snapshot = SettingsSnapshot.Defaults(PlaceholderArrSyncInterval);

    public static TimeSpan FreshUntilAge => Snapshot.FreshUntil;

    public static TimeSpan ServeUntilAge => Snapshot.ServeUntil;

    public static TimeSpan ActiveWindow => Snapshot.ActiveWindow;

    public static TimeSpan RefreshLead => Snapshot.RefreshLead;

    public static TimeSpan WorkerCycleInterval => Snapshot.WorkerCycleInterval;

    public static bool WorkerEnabled => Snapshot.WorkerEnabled;

    // Worker-pacing knobs with no SettingsSnapshot field yet (not in the persisted settings catalog).
    public static TimeSpan RepopulationSpreadWindow => FreshUntilAge;

    public const int MaxConcurrentRefreshes = 4;
}
