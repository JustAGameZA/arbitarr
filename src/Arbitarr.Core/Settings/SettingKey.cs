namespace Arbitarr.Core.Settings;

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

    /// <summary>
    /// Global shadow-mode toggle spanning ALL suppression sources (deterministic rule engine,
    /// identity layer, numbering scorer, AI verdicts). Defaults ON for fresh installs (D3):
    /// suppressions are recorded/annotated but never enforced until the operator flips this off.
    /// </summary>
    ShadowMode,

    /// <summary>
    /// Admin API key gating all <c>Arbitarr.Api.Routing.RouteClassification.AdminMutating</c>
    /// routes (D2, wired at M7). Distinct from the Torznab/Newznab client apikey
    /// (<see cref="Arbitarr.Core.Security.IClientApiKeyResolver"/>) and from the upstream NZBHydra2
    /// key — this key exists solely so the admin UI's mutating endpoints are not open to anyone who
    /// can reach the LAN port.
    /// </summary>
    AdminApiKey,

    /// <summary>
    /// Minimum AI verdict confidence required to suppress a release (D3). Default 0.9.
    /// </summary>
    AiConfidenceThreshold,

    /// <summary>
    /// Kill-switch for AI title normalization (M5-8/AC26b). Defaults <b>OFF</b>: normalization
    /// (allow-list, deny-list, differential-parse guard) only runs when an operator explicitly
    /// enables it, so a fresh install never risks altering titles the *arr apps rely on to parse
    /// releases until reviewed.
    /// </summary>
    TitleNormalizationEnabled,

    /// <summary>
    /// Poll interval for the classifier's background hosted-service loop. Re-read from
    /// settings at the top of every cycle (AC24) — changing it takes effect on the next cycle, no
    /// restart required.
    /// </summary>
    ClassifierPollInterval,

    /// <summary>
    /// AC26b: boolean kill-switch that fully disables the AI layer. Unlike the other settings here,
    /// this is boolean rather than floor/ceiling-bounded — it is a safety escape hatch, not a
    /// preference, so the usual bound-validation convention does not apply.
    /// </summary>
    AiKillSwitch,

    /// <summary>
    /// AC14b: the human-latency budget for the ad-hoc search endpoint's synchronous-AI opt-in
    /// (<c>Arbitarr.Core.Arbitration.ISyncReleaseArbiter</c>). Distinct from the AC14 machine
    /// (Torznab/Newznab) budget — this bounds how long a human's ad-hoc search waits per candidate
    /// for a live Ollama call before that candidate fails open to Unknown (P1).
    /// </summary>
    SyncArbitrationBudget,
}
