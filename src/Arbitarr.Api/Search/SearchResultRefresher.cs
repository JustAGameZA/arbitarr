using Arbitarr.Core.Caching;

namespace Arbitarr.Api.Search;

/// <summary>
/// The <see cref="RefreshFetcher"/> the proactive <see cref="RefreshWorker"/> is handed by the Host:
/// re-runs the <see cref="CachedSearchPayload.Query"/> persisted inside a stale entry through the
/// same <see cref="UpstreamMergeStage"/> the inline path uses and hands back the new payload.
///
/// <para>
/// Returns null (leave the entry untouched, M3-10) when the stored payload cannot be read, or when
/// the merge came back degraded and empty — a rate-limited/failed upstream must never overwrite the
/// good data already held. Exceptions from the merge propagate so the worker records them on the
/// shared circuit breaker.
/// </para>
///
/// <para><b>This type is the SECOND writer of a <see cref="CachedSearchPayload"/> row, and it writes
/// the row <see cref="PaginationSnapshotService"/> wrote (both key it through
/// <c>SearchResultCacheStage.BuildQueryKey</c>).</b> It therefore has to apply
/// <see cref="DedupStage"/> at the same point in its own pipeline — after the merge, before
/// serialising — or the proactive <see cref="RefreshWorker"/> would replace a grouped payload with
/// an ungrouped one. That is exactly the failure the inline path's placement comment says the
/// cache-side placement prevents, reintroduced off the request path where nothing would report it:
/// the entry would simply stop being grouped some minutes after it was written, and only for
/// queries the worker happened to refresh. A reader cannot tell a never-grouped row from a
/// regrouped one, so this is a silent divergence, not a visible one.</para>
/// </summary>
public sealed class SearchResultRefresher
{
    private readonly UpstreamMergeStage _mergeStage;
    private readonly DedupStage _dedupStage;

    /// <param name="dedupStage">
    /// Required, not optional. A refresher constructed without one would silently write the
    /// ungrouped payload this type exists to avoid writing, and the defect would be invisible at
    /// the call site — so "no dedup" is not offered as a constructible state.
    /// </param>
    public SearchResultRefresher(UpstreamMergeStage mergeStage, DedupStage dedupStage)
    {
        _mergeStage = mergeStage ?? throw new ArgumentNullException(nameof(mergeStage));
        _dedupStage = dedupStage ?? throw new ArgumentNullException(nameof(dedupStage));
    }

    public async Task<string?> RefreshAsync(CachedSearchResult entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var payload = CachedSearchPayload.Deserialize(entry.PayloadJson);
        if (payload is null)
        {
            return null;
        }

        var merged = await _mergeStage.MergeAsync(payload.Query, cancellationToken).ConfigureAwait(false);
        if (merged.Releases.Count == 0 && merged.RateLimitedSources.Count > 0)
        {
            return null;
        }

        // Dedup here, at the same point the inline path does it (after the merge, before the
        // payload is built), so both writers of this row produce the same shape. The rate-limit
        // guard above deliberately stays on merged.Releases: whether upstream returned anything at
        // all is a question about the FETCH, and grouping could only ever shrink that count, so
        // asking it after dedup would let a fully-duplicated-but-healthy result look degraded.
        var deduplicated = _dedupStage.Deduplicate(merged.Releases);

        return new CachedSearchPayload(payload.Query, deduplicated).Serialize();
    }
}
