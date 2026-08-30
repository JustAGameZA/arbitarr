using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Search;

/// <summary>
/// SEC-M4: clamps attacker/client-controlled paging inputs (<c>limit</c>/<c>offset</c>
/// query-string parameters) at the endpoint boundary, independent of what any downstream
/// component happens to do with them. Upper bound mirrors the most a single search could ever
/// legitimately page through: our own enforced page-size ceiling multiplied by the per-source
/// cap on upstream calls per search.
/// </summary>
public static class PagingClamp
{
    // NzbHydraSourceOptions.MaxUpstreamCallsPerSearch default (an instance-level option, not a
    // static constant reachable from here) is 3; this mirrors that default rather than being
    // wired to the live per-source value.
    public const int MaxLimit = CapsAggregator.EnforcedMaxPageSize * 3;

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? 100, 1, MaxLimit);

    public static int ClampOffset(int? offset) => Math.Max(offset ?? 0, 0);
}
