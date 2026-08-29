namespace ArrSearcher.Media.Providers;

/// <summary>
/// Configuration for a single <see cref="ArrApiProvider"/> instance.
/// </summary>
/// <param name="BaseUrl">Base URL of the *arr instance (Sonarr/Radarr), e.g. http://192.0.2.21:8989.</param>
/// <param name="ApiKey">*arr API key. Passed as a query-string parameter to every request, never as a header, never logged.</param>
/// <param name="SourceName">
/// Stable name identifying this provider instance for circuit-breaker lookups and provenance
/// (<see cref="ArrSearcher.Core.Identity.MatchProvenance.Scheme"/>-adjacent labelling). Defaults to
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
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(10);
}
