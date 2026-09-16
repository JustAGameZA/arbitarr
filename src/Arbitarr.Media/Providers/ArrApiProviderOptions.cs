using Arbitarr.Core.Diagnostics;

namespace Arbitarr.Media.Providers;

/// <summary>
/// Configuration for a single <see cref="ArrApiProvider"/> instance.
/// </summary>
/// <param name="BaseUrl">Base URL of the *arr instance (Sonarr/Radarr), e.g. http://192.0.2.21:8989.</param>
/// <param name="ApiKey">*arr API key. Passed as a query-string parameter to every request, never as a header, never logged.</param>
/// <param name="SourceName">
/// Stable name identifying this provider instance for circuit-breaker lookups and provenance
/// (<see cref="Arbitarr.Core.Identity.MatchProvenance.Scheme"/>-adjacent labelling). Defaults to
/// "ArrApi" to match the Q5-D preference-order naming in the plan.
/// </param>
/// <param name="RequestTimeout">
/// Per-HTTP-request timeout. This is a single authoritative lookup (no fan-out/pagination), so it
/// stays comfortably under AC14's overall ≤12s SearchAsync budget; defaults to 10s.
/// </param>
public sealed record ArrApiProviderOptions(
    Uri BaseUrl,
    string ApiKey,
    string SourceName = "ArrApi",
    TimeSpan? RequestTimeout = null)
{
    /// <summary>
    /// The per-request timeout used when <see cref="RequestTimeout"/> is not given.
    /// </summary>
    /// <remarks>
    /// Exposed separately because the composition root needs it WITHOUT an options instance: since
    /// arb-u1c the named client's timeout is set once at registration rather than by
    /// <c>ArrApiProvider</c>'s constructor (which cannot re-time a pooled client), and at that point
    /// there is no base URL or key to build an options record around.
    /// </remarks>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? DefaultRequestTimeout;

    /// <summary>
    /// Renders the address and source name in full and the key as
    /// <see cref="CredentialPatterns.Replacement"/> (arb-1ox9).
    /// </summary>
    /// <remarks>
    /// <para><b>THIS IS THE MECHANISM BEHIND <see cref="ApiKey"/>'s "never logged".</b> That param doc
    /// is a comment, and a comment cannot fail — the synthesised <c>ToString</c> on a positional
    /// record prints every member by name and value, so <c>$"provider options {options}"</c> would
    /// have emitted the key verbatim while the doc above it still read as correct.</para>
    ///
    /// <para><b>The key's REQUEST shape being covered is exactly what makes this shape easy to
    /// overlook</b> (CLAUDE.md §1). The key IS sent as a query-string parameter, so an outbound URI
    /// carrying it is collapsed by <c>IHttpClientFactory</c>'s redaction — but that redaction sees
    /// only request URIs, never this record. <c>LogMessageCleanser</c> does scrub a rendered
    /// <c>ApiKey = …</c>, and <b>only in the LOG SINK</b>: an exception message or console line
    /// carrying these options never meets it, while <c>ToString</c> is where all of those are formed.
    /// The sink's coverage is name-dependent rather than structural — see
    /// <c>Arbitarr.Data.Security.CreatedApiKey.ToString</c> for the sibling spelling it misses.</para>
    /// </remarks>
    public override string ToString() =>
        $"{nameof(ArrApiProviderOptions)} {{ {nameof(BaseUrl)} = {BaseUrl}, "
        + $"{nameof(ApiKey)} = {CredentialPatterns.Replacement}, "
        + $"{nameof(SourceName)} = {SourceName} }}";
}
