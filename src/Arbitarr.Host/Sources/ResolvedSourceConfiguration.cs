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
    ///
    /// <para><b>arb-72mf: this is no longer what the dashboard reports.</b> Everything above still
    /// holds and is still the meaning of THIS property, which remains NZBHydra-only on purpose — it
    /// is the legacy single-source leg, read alongside <see cref="BaseUrl"/> and
    /// <see cref="SourceName"/>, which describe that one row and nothing else. What moved is the
    /// dashboard's question: <c>nzbHydraConfigured</c> is now fed by
    /// <see cref="AnySourceConfigured"/> below. Do not re-point that field at this property.</para>
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// arb-72mf: whether AT LEAST ONE ENABLED SOURCE OF ANY KIND carries an API key. This is what
    /// <c>nzbHydraConfigured</c> on <c>/api/config/effective</c> reports, and what the Dashboard's
    /// not-configured empty state reads.
    ///
    /// <para><b>Why a second flag rather than widening <see cref="IsConfigured"/>.</b> Since #344 an
    /// install may have only direct Newznab/Torznab rows, which <c>SourceRegistry</c> resolves and
    /// searches perfectly well. <see cref="IsConfigured"/> is derived from <see cref="ApiKey"/>,
    /// which <c>SourceSeeder.ResolveFromDatabaseAsync</c> only ever populates from a
    /// <c>Kind == NzbHydraKind &amp;&amp; Enabled</c> row, so such an install reported
    /// <c>nzbHydraConfigured:false</c> and the Dashboard showed the #50 not-configured empty state
    /// while searching correctly. Widening <see cref="IsConfigured"/> in place was rejected because
    /// it is read as the NZBHydra leg's own answer beside <see cref="BaseUrl"/> and
    /// <see cref="SourceName"/> (which stay NZBHydra-only and would then disagree with it), and
    /// because the enabled-with-key reasoning recorded above is specifically about that one row.</para>
    ///
    /// <para><b>Enabled-with-key, for the same reason 53d settled it for one source.</b> #50 exists
    /// to distinguish "configured" from "reporting". A disabled row will not be searched, and a row
    /// with no key cannot be queried, so neither can make an install configured — reporting
    /// otherwise would tell the operator the setup is fine while nothing can be queried, which is
    /// precisely the misleading state #50 was written to prevent.</para>
    ///
    /// <para><b>Presence, never the value.</b> This is computed from the EXISTENCE of the
    /// write-only <c>source:{id}:api_key</c> row, through the same presence path
    /// <c>SourceRepository.HasApiKeyAsync</c> uses. It does not read a key, and must never be
    /// changed to: <c>ReadApiKeyForUpstreamRequestAsync</c> has exactly one caller per secret family
    /// (CLAUDE.md section 1, pinned by <c>SecretReaderSingleCallerTests</c>), and a "configured"
    /// check is not a credential consumer.</para>
    ///
    /// <para>Resolved once at startup, exactly like the rest of this type, so enabling a source in
    /// the UI changes this only after a restart — the existing 53b design, unchanged here.</para>
    /// </summary>
    public bool AnySourceConfigured { get; private set; }

    /// <summary>
    /// Publishes the resolved configuration. Called exactly once, from Host startup, before the
    /// application begins serving.
    /// </summary>
    /// <param name="anySourceConfigured">
    /// arb-72mf: whether any enabled source of any kind has a key. Independent of the other three
    /// arguments, which describe the NZBHydra row alone — an install with only Newznab rows passes
    /// true here with a null <paramref name="baseUrl"/>.
    /// </param>
    public void Apply(string? baseUrl, string? apiKey, string? sourceName, bool anySourceConfigured)
    {
        BaseUrl = baseUrl;
        ApiKey = apiKey;
        SourceName = sourceName;
        AnySourceConfigured = anySourceConfigured;
    }
}
