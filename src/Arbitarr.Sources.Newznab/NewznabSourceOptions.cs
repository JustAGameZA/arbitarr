namespace Arbitarr.Sources.Newznab;

/// <summary>
/// Configuration for a single <see cref="NewznabSource"/> instance — one direct Newznab or Torznab
/// indexer.
///
/// <para><b>An options record, not the Source entity.</b> These values originate in the
/// <c>Sources</c> table, but this adapter takes them as a plain record so the project needs no
/// reference to Arbitarr.Data: a wire-protocol adapter that depended on the persistence schema
/// would have to change whenever a column it does not read is added. Projecting a row onto this
/// record is the registry's job (arb-x7w8.4).</para>
/// </summary>
/// <param name="BaseUrl">Base URL of the indexer, e.g. http://indexer.example:9117. Must be absolute http/https.</param>
/// <param name="ApiPath">
/// The path under <paramref name="BaseUrl"/> at which this indexer serves its Newznab/Torznab API,
/// taken verbatim from the Source row's <c>ApiPath</c> column.
///
/// <para><b>This is read, never derived.</b> Real deployments do not agree on it — Jackett serves
/// <c>/api/v2.0/indexers/…/results/torznab</c>, NZBHydra2 serves <c>/api</c>, and a reverse proxy
/// relocates either at will — so a hardcoded switch on the source kind would be wrong for any
/// deployment that is not the default, with no way for an operator to correct it short of a code
/// change.</para>
/// </param>
/// <param name="ApiKey">The indexer's API key. Passed as a query-string parameter to every request, never as a header, never as a path segment.</param>
/// <param name="SourceName">Stable name identifying this source instance (rate-limiter keying, circuit-breaker lookups, and <see cref="Arbitarr.Core.Sources.IUpstreamSource.Name"/>).</param>
/// <param name="RequestTimeout">
/// Per-HTTP-request timeout, from the Source row's <c>TimeoutSeconds</c> column when the operator
/// set one. <c>null</c> means fall back to <see cref="DefaultRequestTimeout"/> rather than "no
/// timeout" — a source that has never been tuned must not behave differently from one whose
/// operator explicitly chose the default.
/// </param>
/// <param name="MaxUpstreamPageSize">Per-request result ceiling used to size a page. A direct indexer advertises its own in caps (<c>&lt;limits max&gt;</c>); until the registry feeds that through this is a conservative default.</param>
/// <param name="MaxUpstreamCallsPerSearch">Hard cap on the number of paged upstream HTTP calls a single SearchAsync invocation may issue, regardless of the requested Limit.</param>
/// <param name="RateLimitMaxCalls">Token-bucket capacity for the per-source rate limiter.</param>
/// <param name="RateLimitInterval">Token-bucket refill window for the per-source rate limiter.</param>
public sealed record NewznabSourceOptions(
    Uri BaseUrl,
    string ApiPath,
    string ApiKey,
    string SourceName,
    TimeSpan? RequestTimeout = null,
    int MaxUpstreamPageSize = 100,
    int MaxUpstreamCallsPerSearch = 3,
    int RateLimitMaxCalls = 5,
    TimeSpan? RateLimitInterval = null)
{
    /// <summary>
    /// The per-request timeout used when the Source row set no <c>TimeoutSeconds</c> override. Kept
    /// well under AC14's ≤12s total SearchAsync budget so that even a fan-out of several calls
    /// stays inside it.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The per-request timeout actually applied: the operator's override, else the default.</summary>
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? DefaultRequestTimeout;

    /// <summary>The token-bucket refill window actually applied.</summary>
    public TimeSpan EffectiveRateLimitInterval => RateLimitInterval ?? TimeSpan.FromSeconds(1);
}
