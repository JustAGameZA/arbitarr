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

    /// <summary>D3 global shadow-mode toggle spanning every suppression source.</summary>
    Filtering,
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
        new SettingCatalogEntry(
            SettingKey.SyncArbitrationBudget,
            SettingGroup.Ai,
            "Sync AI arbitration budget (ad-hoc search)",
            "AC14b human-latency budget: how long the ad-hoc search's optional synchronous-AI " +
            "arbitration waits per release before that release fails open to an Unknown verdict. " +
            "Floor 1s (below this every call would fail open, defeating the opt-in). Ceiling 30s " +
            "(above this the admin UI feels unresponsive). Distinct from the AC14 machine budget.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.ShadowMode,
            SettingGroup.Filtering,
            "Shadow mode",
            "D3 global shadow toggle across every suppression source (rule engine, identity layer, " +
            "numbering scorer, AI verdicts). ON (the fresh-install default): suppressions are recorded " +
            "and annotated but never enforced, so nothing is withheld from the *arr apps until you have " +
            "reviewed the suppression log. OFF: suppressions are enforced.",
            RequiresRestart: false,
            IsBoolean: true),
        new SettingCatalogEntry(
            SettingKey.AiConfidenceThreshold,
            SettingGroup.Ai,
            "AI confidence threshold",
            "D3 minimum verdict confidence for an AI suppression to count. Must be strictly greater than " +
            "0 (a 0 threshold would suppress on any verdict, silently defeating the AI slot of the " +
            "allow > deny > AI > pass chain) and at most 1 (a confidence is a probability). Default 0.9; " +
            "tune only after reviewing verdict-vs-confidence data from a soak.",
            RequiresRestart: false,
            IsBoolean: false),
        new SettingCatalogEntry(
            SettingKey.TitleNormalizationEnabled,
            SettingGroup.Ai,
            "AI title normalization",
            "AC26b kill-switch for AI title normalization (allow-list, deny-list, differential-parse " +
            "guard). Defaults OFF so a fresh install never alters titles the *arr apps parse; enable " +
            "only after reviewing normalization output in the match-explanation view.",
            RequiresRestart: false,
            IsBoolean: true),
        new SettingCatalogEntry(
            SettingKey.ClassifierPollInterval,
            SettingGroup.Ai,
            "Classifier poll interval",
            "How often the background classifier loop polls for unclassified releases. Floor 15s " +
            "(prevents a hot loop against the release lookup). No ceiling: a long interval only delays " +
            "classification, it cannot cause silent wrongness. Re-read at the top of every cycle, so " +
            "changes apply on the next cycle without restart.",
            RequiresRestart: false,
            IsBoolean: false),
    };

    /// <summary>
    /// Returns the code-defined default for <paramref name="key"/>, as the exact CLR type the
    /// caller is expected to interpret the setting's value as (see each <see cref="SettingKey"/>
    /// member's own XML doc for the type/semantics). Throws <see cref="ArgumentOutOfRangeException"/>
    /// for a key with no declared default — every settings key must have one.
    /// </summary>
    public static object GetDefault(SettingKey key) => key switch
    {
        SettingKey.ShadowMode => true,
        SettingKey.AiConfidenceThreshold => 0.9,
        SettingKey.TitleNormalizationEnabled => false,
        SettingKey.ClassifierPollInterval => TimeSpan.FromMinutes(1),
        SettingKey.SyncArbitrationBudget => TimeSpan.FromSeconds(5),
        _ => throw new ArgumentOutOfRangeException(
            nameof(key), key, $"No default declared in {nameof(SettingsCatalog)} for '{key}'."),
    };
}
