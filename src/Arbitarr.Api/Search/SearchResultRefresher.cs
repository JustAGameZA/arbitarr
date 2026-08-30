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
/// </summary>
public sealed class SearchResultRefresher
{
    private readonly UpstreamMergeStage _mergeStage;

    public SearchResultRefresher(UpstreamMergeStage mergeStage)
    {
        _mergeStage = mergeStage ?? throw new ArgumentNullException(nameof(mergeStage));
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

        return new CachedSearchPayload(payload.Query, merged.Releases).Serialize();
    }
}
