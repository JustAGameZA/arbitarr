namespace Arbitarr.Core.Caching;

/// <summary>
/// Outcome of <see cref="SearchResultCache.GetAsync"/>: what to serve, if anything, plus the
/// provenance (age/band) every served set must carry (AC-M7a-cache/M3-5).
/// </summary>
/// <param name="Band">Which band the entry fell into at read time, or the classification that led to no entry being served.</param>
/// <param name="PayloadJson">The cached payload to serve, or null when nothing is servable from cache.</param>
/// <param name="Age">Age of the served payload at read time, or null when nothing is servable.</param>
/// <param name="RefreshTriggered">Whether a secondary live-attempt/refresh was triggered for this read (Stale-but-valid band only).</param>
public sealed record CacheReadResult(
    CacheBand Band,
    string? PayloadJson,
    TimeSpan? Age,
    bool RefreshTriggered)
{
    /// <summary>True when <see cref="PayloadJson"/> is non-null and safe to serve as-is.</summary>
    public bool IsServable => PayloadJson is not null;
}
