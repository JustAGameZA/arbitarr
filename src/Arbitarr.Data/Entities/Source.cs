namespace Arbitarr.Data.Entities;

/// <summary>
/// A configured upstream indexer/source (#53, stage 53a — persistence only). The API key for this
/// source is deliberately NOT a property here: per the plan's §3.1 design decision, secrets live as
/// write-only rows in the existing <see cref="SettingEntry"/> table (keyed by
/// <see cref="Sources.SourceRepository.ApiKeySettingName"/>), never on this entity, so a query or
/// serialization of <see cref="Source"/> can never leak a key by accident.
///
/// Stage 53a writes these rows but nothing reads them yet — env vars remain authoritative until
/// 53b adds the DB-first, env-var-fallback resolution path described in the plan's §3.2.
///
/// <para><b>WHAT DELIBERATELY DOES NOT BELONG ON THIS ENTITY.</b> This row is an operator's
/// CONFIGURATION of a source — what they typed, and what they would expect to get back after a
/// restore. Three things the direct-indexer work needs are therefore NOT columns here, and adding
/// them would be a mistake rather than a convenience:
/// <list type="bullet">
/// <item><description>The <b>caps/capabilities cache</b> (arb-x7w8.2) — what the indexer answered
/// when asked what it supports. Fetched from upstream, refreshed on a TTL, and reconstructible by
/// asking again; it is cached upstream state, not configuration.</description></item>
/// <item><description>The <b>per-source query and grab counters, and their rolling window start</b>
/// (arb-x7w8.5) — the running tallies <see cref="QueryLimit"/>/<see cref="GrabLimit"/> are compared
/// against. These change on every search, so putting them here would rewrite the config row
/// constantly and make a restored backup silently re-assert a stale window.</description></item>
/// <item><description>The <b>last error and health state</b> (arb-x7w8.6) — transient runtime
/// observation, already modelled that way by <see cref="SourceHealthRecord"/>.</description></item>
/// </list>
/// Each belongs in its own table, following the <see cref="DownloadRefusalEntry"/> precedent for
/// runtime state that references a source by name rather than living on it. The concrete harm in
/// collapsing them into this row: they would ride into every configuration backup (which #56 scopes
/// to config, not telemetry), and a restore would reinstate counters and cached capabilities that
/// were true at backup time and are not true now.</para>
/// </summary>
public sealed class Source
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The source implementation this row configures, e.g. "NzbHydra". Stored as a string rather
    /// than an enum so a new source kind never requires a schema migration to add.
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>Operator-facing name shown in the UI (53d) and logs (53b); must be unique.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Base URL of the upstream source, e.g. http://192.0.2.21:5076. Must be absolute http/https.</summary>
    public required string BaseUrl { get; set; }

    /// <summary>Whether this source participates in searches. Disabling never deletes the row.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The path under <see cref="BaseUrl"/> at which this source serves its Newznab/Torznab API,
    /// e.g. <c>/api</c>. Modelled on Prowlarr's <c>NewznabSettings.ApiPath</c>, which exists because
    /// real deployments do not agree: Jackett serves <c>/api/v2.0/indexers/…/results/torznab</c>,
    /// NZBHydra2 serves <c>/api</c>, and reverse proxies relocate either at will.
    ///
    /// <para>This column exists so the adapter (arb-x7w8.2) READS the path rather than hardcoding
    /// one — a hardcoded switch on <see cref="Kind"/> would be wrong for any deployment that is not
    /// the default, and an operator would have no way to correct it without a code change.</para>
    /// </summary>
    public string ApiPath { get; set; } = "/api";

    /// <summary>
    /// Ordering and dedup tiebreak weight; higher wins. Read by arb-x7w8.8's result merge to decide
    /// which member of an exact-match group is presented first. Zero is the neutral default, so a
    /// source added without an opinion never displaces one an operator deliberately ranked.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Per-source request timeout override in seconds. <c>null</c> means fall back to the global
    /// default rather than "no timeout" — a source that has never been tuned must not behave
    /// differently from one whose operator explicitly chose the global value.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Maximum searches permitted against this source per <see cref="LimitsUnit"/> window.
    ///
    /// <para><b>null (unlimited) is NOT 0.</b> They are two different states and must round-trip as
    /// two different states. <c>null</c> means the source imposes no query cap; <c>0</c> means a cap
    /// of zero, i.e. the source is exhausted before it starts. Both NZBHydra2
    /// (<c>Optional&lt;Integer&gt;</c>) and Prowlarr model the limit this way. Collapsing
    /// <c>null</c> to <c>0</c> silently disables an unlimited indexer; collapsing <c>0</c> to
    /// <c>null</c> lets a limited one run free past the cap its operator set. Any code that reads
    /// this — a merge, a UI form, a DTO projection — must preserve the distinction.</para>
    /// </summary>
    public int? QueryLimit { get; set; }

    /// <summary>
    /// Maximum grabs permitted against this source per <see cref="LimitsUnit"/> window.
    ///
    /// <para><b>null (unlimited) is NOT 0</b>, for exactly the reasons given on
    /// <see cref="QueryLimit"/>: <c>null</c> is no cap, <c>0</c> is a cap of zero, and collapsing
    /// either into the other silently changes what the source is allowed to do.</para>
    /// </summary>
    public int? GrabLimit { get; set; }

    /// <summary>
    /// The rolling window <see cref="QueryLimit"/> and <see cref="GrabLimit"/> are counted over:
    /// <c>"Hour"</c> or <c>"Day"</c>, matching the windows Prowlarr's <c>IndexerLimitService</c>
    /// uses.
    ///
    /// <para>Stored as a string rather than an enum for the same reason <see cref="Kind"/> is: a
    /// new window never requires a schema migration to add. Validation happens at the repository
    /// boundary by exact ordinal name — do not convert this to an enum.</para>
    /// </summary>
    public string LimitsUnit { get; set; } = "Day";

    /// <summary>
    /// How an NZB/torrent download from this source is served to the client: <c>"Proxy"</c> (Arbitarr
    /// fetches it upstream and streams the bytes, so the indexer key never leaves the server) or
    /// <c>"Redirect"</c> (Arbitarr answers with a <c>Location</c> pointing at the upstream URL,
    /// which carries the key to the client).
    ///
    /// <para><b>Today the only writable value is <c>"Proxy"</c>.</b> <c>"Redirect"</c> is described
    /// here because it is the mode this column exists to eventually carry, but it is NOT accepted
    /// yet: <c>SourceRepository.KnownNzbAccessModes</c> deliberately omits it, so a write of
    /// <c>"Redirect"</c> is rejected with a 400 by construction rather than by an operator
    /// remembering not to. arb-x7w8.14 is the bead that admits it, together with the Settings UI
    /// warning that the key is exposed to the client in that mode — the warning and the capability
    /// ship in the same change, so neither can arrive without the other.</para>
    ///
    /// <para>Defaults to <c>"Proxy"</c> so a newly added source never silently exposes its key. A
    /// default of <c>"Redirect"</c> would leak a key on the first download after an operator did
    /// nothing but add a source.</para>
    /// </summary>
    public string NzbAccessMode { get; set; } = "Proxy";

    /// <summary>When this source was first configured.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this source was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
