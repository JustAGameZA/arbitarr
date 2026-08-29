namespace ArrSearcher.Core.Settings;

/// <summary>
/// The fixed catalog of persisted retention/TTL settings (plan lines ~1043-1056). Each key
/// corresponds to one row in the settings store (worker-owned schema) and one entry in
/// <see cref="SettingsCatalog"/> describing its default/floor/ceiling policy.
/// </summary>
public enum SettingKey
{
    /// <summary>Search-result cache "served directly, zero upstream requests" age.</summary>
    FreshUntil,

    /// <summary>Search-result cache outer availability-fallback age; entries older are not served at all.</summary>
    ServeUntil,

    /// <summary>Worker: trailing window defining "actively being requested".</summary>
    ActiveWindow,

    /// <summary>Worker: how far ahead of FreshUntil the worker aims to refresh.</summary>
    RefreshLead,

    /// <summary>Worker: how often the worker wakes and evaluates the selection predicate.</summary>
    WorkerCycleInterval,

    /// <summary>Worker: global on/off for proactive refresh.</summary>
    WorkerEnabled,

    /// <summary>AI verdict cache TTL eviction on last-access.</summary>
    AiVerdictCacheTtl,

    /// <summary>AI verdict cache LRU trim row ceiling.</summary>
    AiVerdictCacheRowCeiling,

    /// <summary>Metadata/identity cache refresh cadence (positive entries).</summary>
    MetadataRefreshCadence,

    /// <summary>Metadata/identity cache negative-entry ("no coverage") TTL.</summary>
    MetadataNegativeTtl,

    /// <summary>Suppression audit log retention window.</summary>
    SuppressionAuditRetention,

    /// <summary>Query snapshot TTL (pagination-scoped).</summary>
    QuerySnapshotTtl,

    /// <summary>Maintenance job (prune + vacuum) scheduling interval. Restart-required exception.</summary>
    MaintenanceJobInterval,
}
