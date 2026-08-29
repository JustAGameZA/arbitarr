namespace Arbitarr.Core.Settings;

/// <summary>
/// The full set of current setting values, needed because several bounds are cross-field
/// (e.g. <see cref="ServeUntil"/>'s floor is the *current* <see cref="FreshUntil"/> value, and
/// <see cref="FreshUntil"/>'s ceiling depends on the AC0c-measured *arr RSS sync interval, which
/// is environment data rather than a setting). Validating a proposed change to one setting
/// requires knowing the others' current values.
/// </summary>
/// <param name="FreshUntil">Search-result cache "served directly" age.</param>
/// <param name="ServeUntil">Search-result cache outer availability-fallback age.</param>
/// <param name="ActiveWindow">Worker "actively being requested" trailing window.</param>
/// <param name="RefreshLead">Worker: how far ahead of FreshUntil to refresh.</param>
/// <param name="WorkerCycleInterval">Worker scan period.</param>
/// <param name="WorkerEnabled">Global on/off for proactive refresh.</param>
/// <param name="AiVerdictCacheTtl">AI verdict cache TTL (last-access eviction).</param>
/// <param name="AiVerdictCacheRowCeiling">AI verdict cache LRU trim row ceiling.</param>
/// <param name="MetadataRefreshCadence">Metadata/identity cache refresh cadence (positive entries).</param>
/// <param name="MetadataNegativeTtl">Metadata/identity cache negative-entry TTL.</param>
/// <param name="SuppressionAuditRetention">Suppression audit log retention window.</param>
/// <param name="QuerySnapshotTtl">Query snapshot TTL.</param>
/// <param name="MaintenanceJobInterval">Maintenance job (prune + vacuum) interval.</param>
public sealed record SettingsSnapshot(
    TimeSpan FreshUntil,
    TimeSpan ServeUntil,
    TimeSpan ActiveWindow,
    TimeSpan RefreshLead,
    TimeSpan WorkerCycleInterval,
    bool WorkerEnabled,
    TimeSpan AiVerdictCacheTtl,
    int AiVerdictCacheRowCeiling,
    TimeSpan MetadataRefreshCadence,
    TimeSpan MetadataNegativeTtl,
    TimeSpan SuppressionAuditRetention,
    TimeSpan QuerySnapshotTtl,
    TimeSpan MaintenanceJobInterval)
{
    /// <summary>
    /// The plan's documented defaults (table at plan lines ~1043-1056), anchored to a 15-minute
    /// AC0c *arr RSS sync interval (the more conservative of Sonarr's 15m / Radarr's 30m per
    /// docs/step0-measurements.md §3).
    /// </summary>
    public static SettingsSnapshot Defaults(TimeSpan arrSyncInterval) => new(
        FreshUntil: TimeSpan.FromMinutes(15),
        ServeUntil: TimeSpan.FromDays(7),
        ActiveWindow: TimeSpan.FromTicks(arrSyncInterval.Ticks * 24),
        RefreshLead: TimeSpan.FromMinutes(7.5),
        WorkerCycleInterval: TimeSpan.FromMinutes(1),
        WorkerEnabled: true,
        AiVerdictCacheTtl: TimeSpan.FromDays(30),
        AiVerdictCacheRowCeiling: 250_000,
        MetadataRefreshCadence: TimeSpan.FromDays(7),
        MetadataNegativeTtl: TimeSpan.FromDays(30),
        SuppressionAuditRetention: TimeSpan.FromDays(30),
        QuerySnapshotTtl: TimeSpan.FromSeconds(300),
        MaintenanceJobInterval: TimeSpan.FromHours(1));
}
