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
    /// #56: how many automatic configuration backups are kept in the config directory. A COUNT and
    /// not an age — a time-based policy would delete an operator's last backup for a box that was
    /// simply switched off. 0 disables automatic backups entirely.
    /// </summary>
    AutomaticBackupRetainedCount,

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
    /// arb-tps: how long a rendered release stays resolvable by <c>/download/{proxyGuid}</c>.
    ///
    /// <para>Default <b>14 days</b>, and the figure is evidence-driven rather than a round number.
    /// The in-memory lookup's 30 minutes was the whole lifetime before this setting existed, and it
    /// was too short twice over: the process restarts many times a day (29 starts in 25 hours on
    /// the reporting instance, each one wiping the lookup), and Sonarr/Radarr delay profiles
    /// routinely defer a grab well past half an hour — so a link handed to an *arr app frequently
    /// stopped resolving before it was ever used. 14 days comfortably outlives both. The operator
    /// can lower it; the table is bounded by this TTL and pruned on the maintenance pass.</para>
    /// </summary>
    ReleaseLookupTtl,

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

    /// <summary>
    /// #44: how long a login session may sit unused before it stops authenticating. Refreshed by
    /// activity, so an operator working continuously is never signed out mid-task.
    ///
    /// <para>Configurable because the right value is a property of the DEPLOYMENT, not of the
    /// software: a wall-mounted tablet in a locked room and a laptop carried outside the house
    /// warrant opposite answers, and this project cannot know which it is running on. Default 7
    /// days — long enough that a homelab operator checking in weekly is not re-authenticating every
    /// visit, short enough that a forgotten browser stops being a live credential within the week.</para>
    ///
    /// <para>Paired with <see cref="SessionAbsoluteTimeout"/>, which it does not replace: this bound
    /// alone would let a session live forever under anything that touches it periodically.</para>
    /// </summary>
    SessionIdleTimeout,

    /// <summary>
    /// #44: the hard ceiling on a session's life, fixed when it is issued and never extended by
    /// activity. Default 30 days.
    ///
    /// <para>This is the bound that constrains a STOLEN cookie, which is why it cannot be folded
    /// into <see cref="SessionIdleTimeout"/>: an idle timeout does not limit an attacker at all,
    /// because holding the token and using it is what keeps it alive. The two answer different
    /// failure modes — an abandoned browser, and a credential that got out — and both are needed.</para>
    /// </summary>
    SessionAbsoluteTimeout,

    /// <summary>
    /// #89: base URL of the Ollama instance the AI layer classifies against.
    ///
    /// <para><b>Seeded once from <c>Arbitarr:Ai:Ollama:BaseUrl</c>, then the database is
    /// authoritative</b> — the same ruling #53 settled for sources (see
    /// <c>Arbitarr.Host.Ai.OllamaBaseUrlSeeder</c> for the full reasoning and the divergence
    /// warning). After the first start the environment value is inert.</para>
    ///
    /// <para><b>Deliberately NOT in <see cref="SettingsCatalog.Entries"/></b>, and for a different
    /// reason than <see cref="AdminApiKey"/>: this value is not a secret and may be read back
    /// freely. It is off the catalog because it has its OWN section on the Settings surface, with a
    /// connectivity probe the generic catalog row cannot offer; a catalog entry would render it a
    /// second time as an unexplained text field beside the section that already owns it. Its write
    /// path is <c>PUT /api/admin/ai/ollama</c> (<c>AdminAiEndpoints</c>), which reaches
    /// <see cref="SettingsValidator.ValidateOllamaBaseUrl"/> through the same repository switch
    /// every other setting takes.</para>
    /// </summary>
    OllamaBaseUrl,

    /// <summary>
    /// #112: the Ollama model the AI layer sends in every classification request.
    ///
    /// <para><b>Seeded once from <c>Arbitarr:Ai:Ollama:Model</c>, then the database is
    /// authoritative</b> — exactly as <see cref="OllamaBaseUrl"/> is (see
    /// <c>Arbitarr.Host.Ai.OllamaModelSeeder</c>, the sibling of <c>OllamaBaseUrlSeeder</c>). After
    /// the first start the environment value is inert, and a change on the Settings page takes
    /// effect on the next classification through <c>OllamaModelCache</c> +
    /// <c>OllamaModelResolver</c>.</para>
    ///
    /// <para><b>Deliberately NOT in <see cref="SettingsCatalog.Entries"/></b>, for the same reason
    /// as <see cref="OllamaBaseUrl"/> and not <see cref="AdminApiKey"/>'s: this value is not a
    /// secret and is served back on <c>GET /api/admin/ai/ollama</c>. It is off the catalog because
    /// it belongs to the AI section, whose affordance a generic catalog row cannot provide — here a
    /// PICKER populated from the instance's own <c>/api/tags</c> list, which is the entire point of
    /// #112. A catalog entry would render it a second time as an unexplained free-text field beside
    /// the picker that already owns it, and typing a model name blind is the failure this issue
    /// exists to remove. Its write path is <c>PUT /api/admin/ai/ollama</c>
    /// (<c>AdminAiEndpoints</c>), reaching <see cref="SettingsValidator.ValidateOllamaModel"/>
    /// through the same repository switch every other setting takes.</para>
    /// </summary>
    OllamaModel,
}
