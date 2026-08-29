namespace ArrSearcher.Sources.NzbHydra;

/// <summary>
/// Configuration for a single <see cref="NzbHydraSource"/> instance.
/// </summary>
/// <param name="BaseUrl">Base URL of the NZBHydra2 instance, e.g. http://192.0.2.21:5076.</param>
/// <param name="ApiKey">NZBHydra2 API key. Passed as a query-string parameter to every request, never as a header, never logged.</param>
/// <param name="SourceName">Stable name identifying this source instance (used for rate limiting keying, circuit-breaker lookups, and <see cref="IUpstreamSource.Name"/>).</param>
/// <param name="RequestTimeout">Per-HTTP-request timeout. Must stay well under AC14's ≤12s total SearchAsync budget when combined with fan-out; defaults to 10s.</param>
/// <param name="MaxUpstreamPageSize">NZBHydra2's per-request result ceiling. Fixed at 100 per NZBHydra2's own behavior.</param>
/// <param name="MaxUpstreamCallsPerSearch">Hard cap on the number of paged upstream HTTP calls a single SearchAsync invocation may issue, regardless of the requested Limit. See remarks on NzbHydraSource for the chosen default and rationale.</param>
/// <param name="RateLimitMaxCalls">Token-bucket capacity for the per-source rate limiter.</param>
/// <param name="RateLimitInterval">Token-bucket refill window for the per-source rate limiter.</param>
public sealed record NzbHydraSourceOptions(
    Uri BaseUrl,
    string ApiKey,
    string SourceName,
    TimeSpan? RequestTimeout = null,
    int MaxUpstreamPageSize = 100,
    int MaxUpstreamCallsPerSearch = 3,
    int RateLimitMaxCalls = 5,
    TimeSpan? RateLimitInterval = null)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(10);

    public TimeSpan EffectiveRateLimitInterval => RateLimitInterval ?? TimeSpan.FromSeconds(1);
}
