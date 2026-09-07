namespace Arbitarr.Host.Sources;

/// <summary>
/// The NZBHydra2 source configuration actually in force for this process, resolved once at startup
/// by <see cref="SourceSeeder"/> from the <c>Sources</c> table (#53 stage 53b).
///
/// This exists because of an ordering problem the DI container cannot solve on its own: the
/// <c>IUpstreamSource</c> factory and <see cref="Arbitarr.Api.Dashboard.NzbHydraConfigurationStatus"/>
/// are registered before <c>app.Build()</c>, while the database is not migrated (and therefore not
/// safely readable) until after it. Rather than push a DbContext read into a service factory that
/// runs per scope, Host resolves the configuration exactly once, immediately after the migration
/// step, and publishes the answer here as a mutable-once singleton.
///
/// Values are written by <see cref="Apply"/> before the first request is served and only read
/// afterwards, so no synchronisation is required beyond that happens-before ordering.
/// </summary>
public sealed class ResolvedSourceConfiguration
{
    /// <summary>Base URL of the source in force, or null when no source is configured.</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>
    /// API key of the source in force, or null when none is stored. Never logged and never
    /// serialized — <see cref="Arbitarr.Api.Dashboard.NzbHydraConfigurationStatus"/> carries only
    /// the boolean derived from it (AC2, and the #43 secret-leak posture).
    /// </summary>
    public string? ApiKey { get; private set; }

    /// <summary>Display name of the source in force, or null when no source is configured.</summary>
    public string? SourceName { get; private set; }

    /// <summary>
    /// True when a source row is in force and carries an API key. This is what
    /// <c>nzbHydraConfigured</c> on <c>/api/config/effective</c> reports.
    ///
    /// <para><b>53d's aggregate decision (plan §3.4), settled 2026-09-07: "configured" means an
    /// ENABLED source with an API key</b> — not merely "a key exists somewhere".</para>
    ///
    /// <para><b>This is already what the code does, and that is the point of writing it down.</b>
    /// The predicate here reads only <see cref="ApiKey"/>, so on its own it says "has a key". But
    /// <see cref="ApiKey"/> is only ever populated by <c>SourceSeeder.ResolveFromDatabaseAsync</c>,
    /// which selects with <c>Where(s =&gt; s.Kind == NzbHydraKind &amp;&amp; s.Enabled)</c> — a
    /// disabled source resolves to nothing at all, leaving <see cref="ApiKey"/> null and this false.
    /// The enabled-ness is therefore enforced one layer up, in the resolve query, which made the
    /// combined meaning easy to misread from this line alone. 53c's review flagged exactly that risk
    /// ("<c>IsConfigured</c> means 'has a key', NOT 'has an enabled source with a key'"). The
    /// behaviour was already right; this comment is the part that was missing.</para>
    ///
    /// <para><b>Why enabled-with-key is the right meaning, now that 53d surfaces the flag.</b>
    /// #50 exists to distinguish "configured" from "reporting", and a source the operator has
    /// deliberately disabled is not going to be searched. Reporting it as configured on the
    /// dashboard would tell the operator the setup is fine while nothing can possibly be queried —
    /// precisely the misleading state #50 was written to prevent. A disabled source correctly
    /// reads as not configured, and the Dashboard's empty state then says so.</para>
    ///
    /// <para><b>Resolution happens once at startup</b>, so disabling a source in the UI changes this
    /// only after a restart. That is the existing 53b design (the database cannot be read safely
    /// before migrations, so Host resolves once and publishes the answer), not something 53d
    /// changed; the Sources section says as much to the operator.</para>
    ///
    /// <para>Pinned by <c>SourceSeederTests.A_disabled_source_with_a_key_is_not_reported_as_configured</c>.
    /// Do not narrow this predicate to a bare key check without moving the enabled filter with it.</para>
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Publishes the resolved configuration. Called exactly once, from Host startup, before the
    /// application begins serving.
    /// </summary>
    public void Apply(string? baseUrl, string? apiKey, string? sourceName)
    {
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        SourceName = sourceName;
    }
}
