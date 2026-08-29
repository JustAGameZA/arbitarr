using Arbitarr.Core.Identity;

namespace Arbitarr.Media.Cache;

/// <summary>
/// Ties <see cref="IMetadataCacheStore"/> together with a live-fetch callback to implement AC-M8's
/// source-snapshot versioning and AC-M6's three distinctly-recorded degraded states in one place, so
/// providers (e.g. <c>XemProvider</c>) and the matcher never have to reimplement the
/// cache/fetch/invalidate/negative-cache policy themselves.
/// </summary>
/// <remarks>
/// <para>
/// AC-M8: XEM's map data is hand-edited with no changelog and no reliable freshness header (plan
/// risk R7), so this coordinator never trusts HTTP freshness metadata. Every call that reaches the
/// upstream (i.e. every call where the cache was absent, stale-by-schedule, or a caller forces a
/// re-check) hashes the freshly fetched raw content via <see cref="SourceSnapshotHasher"/> and
/// compares it against the cached <c>SourceSnapshotVersion</c>. Only a changed hash invalidates the
/// cached payload; an unchanged hash means the cache stays valid even though a network round trip
/// occurred, and the cache row's <c>RefreshAfter</c>/<c>FetchedAt</c> are still bumped so the next
/// check is scheduled from now.
/// </para>
/// <para>
/// AC-M6: cache-absent, source-unreachable, and no-xem-coverage are reported as distinct
/// <see cref="MatchProvenanceFlags"/> bits (worker-2's contract type — this coordinator does not
/// invent a parallel enum). No-xem-coverage is additionally negative-cached (a confirmed "no map for
/// this series" is itself a cacheable fact) so repeated lookups for the same series do not keep
/// hitting the upstream; a negative-cache hit within its refresh window carries no degradation flag
/// at all, since the cache answered normally with a known-negative fact.
/// </para>
/// </remarks>
public sealed class MetadataCacheCoordinator
{
    private readonly IMetadataCacheStore _store;
    private readonly TimeSpan _refreshInterval;

    /// <param name="store">The backing cache store.</param>
    /// <param name="refreshInterval">
    /// How long a cached entry (positive or negative) is trusted before the next call to
    /// <see cref="ResolveAsync"/> re-fetches to check for upstream change. Defaults to 24 hours.
    /// </param>
    public MetadataCacheCoordinator(IMetadataCacheStore store, TimeSpan? refreshInterval = null)
    {
        _store = store;
        _refreshInterval = refreshInterval ?? TimeSpan.FromHours(24);
    }

    /// <summary>
    /// Resolves metadata for a series/source key: serves a still-fresh cache entry (positive or
    /// negative) without a live fetch, and otherwise fetches live, snapshot-hashes the result, and
    /// only invalidates/rewrites the cache if the hash changed from what was previously stored.
    /// </summary>
    /// <param name="seriesKey">Stable identity key for the series (e.g. TVDB id, series slug).</param>
    /// <param name="source">Name of the upstream metadata source (e.g. "xem").</param>
    /// <param name="fetch">
    /// Performs a live fetch against the upstream when the cache is absent or due for a refresh
    /// check. Not invoked when a still-fresh cache entry (positive or negative) already satisfies
    /// the lookup.
    /// </param>
    /// <param name="now">Current time, for testability. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public async Task<CachedMetadataResult> ResolveAsync(
        string seriesKey,
        string source,
        Func<CancellationToken, Task<MetadataFetchOutcome>> fetch,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);

        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        var cached = await _store.GetAsync(seriesKey, source, cancellationToken);

        if (cached.Kind != MetadataCacheLookupKind.Absent && cached.RefreshAfter is { } refreshAfter && refreshAfter > effectiveNow)
        {
            // Still-fresh cache entry (positive or negative): serve it without a live fetch, and
            // without any degradation flag — the cache answered the question normally.
            return cached.Kind == MetadataCacheLookupKind.NegativeHit
                ? new CachedMetadataResult(null, cached.SourceSnapshotVersion, MatchProvenanceFlags.NoXemCoverage)
                : CachedMetadataResult.Success(cached.PayloadJson!, cached.SourceSnapshotVersion!);
        }

        var fetchOutcome = await fetch(cancellationToken);
        var refreshDeadline = effectiveNow + _refreshInterval;

        switch (fetchOutcome.Kind)
        {
            case MetadataFetchOutcomeKind.Success:
                var hash = SourceSnapshotHasher.ComputeHash(fetchOutcome.RawContent!);
                await _store.SaveAsync(seriesKey, source, fetchOutcome.RawContent!, hash, refreshDeadline, cancellationToken);
                return CachedMetadataResult.Success(fetchOutcome.RawContent!, hash);

            case MetadataFetchOutcomeKind.NoCoverage:
                // Negative caching (AC-M6): the confirmed absence of coverage is itself cached so we
                // don't hammer the endpoint on every subsequent lookup for this series.
                var negativeHash = SourceSnapshotHasher.ComputeHash($"no-coverage:{seriesKey}:{source}");
                await _store.SaveNegativeAsync(seriesKey, source, negativeHash, refreshDeadline, cancellationToken);
                return new CachedMetadataResult(null, negativeHash, MatchProvenanceFlags.NoXemCoverage);

            case MetadataFetchOutcomeKind.Unreachable:
                // Do not touch the existing cache row on an unreachable fetch: a transient outage
                // must not evict or overwrite a still-valid last-known-good (or negative) entry.
                var flags = MatchProvenanceFlags.SourceUnreachable;
                if (cached.Kind == MetadataCacheLookupKind.Absent)
                {
                    flags |= MatchProvenanceFlags.CacheAbsent;
                    return new CachedMetadataResult(null, null, flags);
                }

                // A stale-by-schedule cache row exists (positive or negative); fall back to it as
                // last-known-good rather than surfacing a hard failure, same fallback idiom as
                // ICapsCacheStore's last-known-good behaviour, but still flagged as degraded since
                // the upstream could not be reached to confirm freshness.
                if (cached.Kind == MetadataCacheLookupKind.NegativeHit)
                {
                    return new CachedMetadataResult(null, cached.SourceSnapshotVersion, flags | MatchProvenanceFlags.NoXemCoverage);
                }

                return new CachedMetadataResult(cached.PayloadJson, cached.SourceSnapshotVersion, flags);

            default:
                throw new InvalidOperationException($"Unknown metadata fetch outcome kind: {fetchOutcome.Kind}");
        }
    }
}
