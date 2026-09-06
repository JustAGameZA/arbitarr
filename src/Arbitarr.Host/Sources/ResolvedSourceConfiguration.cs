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
    /// <c>nzbHydraConfigured</c> on <c>/api/config/effective</c> reports; it is deliberately the
    /// same predicate ("an API key is present") that the pre-53b env-var wiring used, so the
    /// endpoint's contract is unchanged (plan §3.4 — the contract change is 53d's decision).
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
