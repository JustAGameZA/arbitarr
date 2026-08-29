namespace Arbitarr.Media.Cache;

/// <summary>
/// SQLite-backed cache for upstream identity/numbering metadata (e.g. XEM's maps), keyed by series
/// and source. Records source-snapshot versioning for invalidation-on-upstream-change (AC-M8) and
/// supports negative caching of confirmed no-coverage results (AC-M6).
/// </summary>
/// <remarks>
/// Deliberately Media-local (not <c>Arbitarr.Core</c>) — unlike <c>ICapsCacheStore</c>, nothing
/// outside <c>Arbitarr.Media</c> currently needs a persistence-agnostic seam for this cache, so
/// introducing one here would be an abstraction with no second implementation. If Core.Identity or
/// another assembly later needs to depend on this cache without pulling in the EF-backed
/// implementation, extract an interface at that point.
/// </remarks>
public interface IMetadataCacheStore
{
    /// <summary>
    /// Looks up the cached entry for a series/source key. Distinguishes cache-absent (no row at
    /// all) from a negative-cache hit (a confirmed "no coverage" result) from a positive hit,
    /// per AC-M6.
    /// </summary>
    Task<MetadataCacheLookup> GetAsync(string seriesKey, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a successfully fetched payload as the new cached value for a series/source key,
    /// stamped with the snapshot hash of the raw fetched content (AC-M8). Overwrites any prior
    /// entry (positive or negative) for the same key.
    /// </summary>
    Task SaveAsync(
        string seriesKey,
        string source,
        string payloadJson,
        string sourceSnapshotVersion,
        DateTimeOffset refreshAfter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a confirmed "no coverage" result for a series/source key as a negative cache entry
    /// (AC-M6), so the no-xem-coverage state itself is cached and the endpoint is not hammered with
    /// repeat lookups for a series confirmed to have no mapping. Overwrites any prior entry for the
    /// same key.
    /// </summary>
    Task SaveNegativeAsync(
        string seriesKey,
        string source,
        string sourceSnapshotVersion,
        DateTimeOffset refreshAfter,
        CancellationToken cancellationToken = default);
}
