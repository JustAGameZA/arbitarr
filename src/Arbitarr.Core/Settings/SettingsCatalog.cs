namespace Arbitarr.Core.Settings;

/// <summary>
/// Logical grouping for the admin settings UI (M7-5). Purely a display/organisation concern —
/// carries no validation semantics of its own.
/// </summary>
public enum SettingGroup
{
    /// <summary>Search-result cache staleness bounds (fresh_until/serve_until).</summary>
    SearchResultCache,

    /// <summary>Proactive-refresh worker scheduling (active_window/refresh_lead/worker_cycle_interval/worker_enabled).</summary>
    Worker,

    /// <summary>AI classifier verdict cache and the AC26b kill-switch.</summary>
    Ai,

    /// <summary>Metadata/identity cache refresh cadence and negative-entry TTL.</summary>
    Metadata,

    /// <summary>Suppression audit log retention.</summary>
    SuppressionAudit,

    /// <summary>Pagination query snapshot TTL.</summary>
    Pagination,

    /// <summary>Maintenance job (prune + vacuum) scheduling.</summary>
    Maintenance,
}

/// <summary>
/// One catalog entry: the admin-UI-facing description of a single setting, independent of its
/// current value. Every field here exists so an operator is never shown a bare, unexplained
/// number — AC24 requires "why this bound exists" to be legible, not just enforced.
/// </summary>
/// <param name="Key">The setting this entry describes.</param>
/// <param name="Group">Which settings-page section this belongs under.</param>
/// <param name="DisplayName">Short human-readable label.</param>
/// <param name="Rationale">Why the floor/ceiling (or, for booleans, the toggle itself) exists.</param>
/// <param name="RequiresRestart">
/// True only for <see cref="SettingKey.MaintenanceJobInterval"/> (see
/// <see cref="SettingsValidator.ValidateMaintenanceJobInterval"/>) — every other setting takes
/// effect on its next evaluation without a restart, per AC24.
/// </param>
/// <param name="IsBoolean">
/// True for boolean toggles (<see cref="SettingKey.WorkerEnabled"/>, <see cref="SettingKey.AiKillSwitch"/>)
/// which have no floor/ceiling — they are on/off, not bounded.
/// </param>
public sealed record SettingCatalogEntry(
    SettingKey Key,
    SettingGroup Group,
    string DisplayName,
    string Rationale,
    bool RequiresRestart,
    bool IsBoolean);

/// <summary>
/// The fixed, ordered catalog of settings the admin UI exposes (M7-5). Deliberately excludes
/// <see cref="SettingKey.AdminApiKey"/> — that key gates the very endpoints this catalog is served
/// through, and is provisioned separately (never displayed or editable via this surface), matching
/// the allow-list-only convention established by <c>ConfigProjection</c> for sensitive values.
/// </summary>
public static class SettingsCatalog
{
    public static readonly IReadOnlyList<SettingCatalogEntry> Entries = new[]
    {
        new SettingCatalogEntry(
            SettingKey.FreshUntil,
            SettingGroup.SearchResultCache,
            "Fresh until",
            "How long a search result is served directly from cache with zero upstream requests. " +
            "Floor 0s (0 disables the healthy-path cache). Ceiling is the lesser of 30m and your " +
            "measured *arr RSS sync interval — any higher and most syncs would see stale cached data.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.ServeUntil,
            SettingGroup.SearchResultCache,
            "Serve until",
            "Outer age at which a cached result is no longer served at all (pruned instead). Floor is " +
            "the current fresh_until (a result can't stop being fresh before it starts). Ceiling 14d.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.WorkerEnabled,
            SettingGroup.Worker,
            "Proactive refresh enabled",
            "Global on/off for the background worker that refreshes cache entries ahead of expiry. " +
            "Turning this off does not affect the search path itself, only whether entries are kept fresh proactively.",
            RequiresRestart: false,
            IsBoolean: true),
        new SettingCatalogEntry(
            SettingKey.ActiveWindow,
            SettingGroup.Worker,
            "Active window",
            "Trailing window defining \"actively being requested\" for refresh prioritisation. Floor is " +
            "your measured *arr RSS sync interval (below this the window expires between consecutive polls " +
            "of the same query and the worker never fires). Ceiling is the lesser of 7d and the current serve_until.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.RefreshLead,
            SettingGroup.Worker,
            "Refresh lead",
            "How far ahead of fresh_until the worker aims to refresh an entry. Floor 1m (prevents refresh " +
            "storms). Ceiling is the current fresh_until (refreshing something that was never stale is pointless).",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.WorkerCycleInterval,
            SettingGroup.Worker,
            "Worker cycle interval",
            "How often the worker wakes to scan for entries needing a refresh. Floor 15s (protects SQLite " +
            "from a hot scan loop). Ceiling is the current refresh_lead (a cycle longer than the lead makes the lead unachievable).",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.AiKillSwitch,
            SettingGroup.Ai,
            "Disable AI layer",
            "Safety escape hatch, not a preference: fully disables AI classification when the AI layer " +
            "is misbehaving or its cost/latency is unacceptable. This is not a quality toggle — flip it only " +
            "if you need AI judgement out of the request path entirely.",
            RequiresRestart: false,
            IsBoolean: true),
        new SettingCatalogEntry(
            SettingKey.AiVerdictCacheTtl,
            SettingGroup.Ai,
            "AI verdict cache TTL",
            "How long an AI verdict is kept before last-access eviction. Floor 24h. No ceiling — verdicts " +
            "are model-version-keyed, so a long TTL cannot cause silent wrongness; size is bounded separately by the row ceiling.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.AiVerdictCacheRowCeiling,
            SettingGroup.Ai,
            "AI verdict cache row ceiling",
            "LRU trim point for the AI verdict cache, in rows. Floor 10,000. No ceiling — an over-large " +
            "value is a disk-space choice visible in the health panel, not a correctness hazard.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.MetadataRefreshCadence,
            SettingGroup.Metadata,
            "Metadata refresh cadence",
            "How often positive metadata/identity cache entries are refreshed. Floor 24h (protects XEM/AniDB " +
            "from over-fetching). Ceiling 30d (above this the instance pins to an indefinitely stale snapshot).",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.MetadataNegativeTtl,
            SettingGroup.Metadata,
            "Metadata negative-entry TTL",
            "How long a \"no coverage\" metadata result is cached before retrying. Same 24h floor / 30d " +
            "ceiling rationale as the positive refresh cadence.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.SuppressionAuditRetention,
            SettingGroup.SuppressionAudit,
            "Suppression audit retention",
            "How long suppression audit log entries are kept. Floor 7d (keeps the shadow-mode 48h review " +
            "window always possible). No ceiling — a longer window only costs disk.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.QuerySnapshotTtl,
            SettingGroup.Pagination,
            "Query snapshot TTL",
            "How long a pagination snapshot is kept alive. Floor 60s. Ceiling 1h (prevents a snapshot " +
            "outliving the paging session it exists to stabilise).",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.MaintenanceJobInterval,
            SettingGroup.Maintenance,
            "Maintenance job interval",
            "How often the prune-and-vacuum maintenance job runs. Floor 5m. Ceiling 24h (above this " +
            "pruning cannot keep pace with accumulation). RESTART REQUIRED: the maintenance job reads " +
            "this once at startup, so a change here only takes effect after the process restarts.",
            RequiresRestart: true,
            IsBoolean: false),
    };
}
