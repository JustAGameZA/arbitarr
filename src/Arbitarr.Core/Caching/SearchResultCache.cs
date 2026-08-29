namespace Arbitarr.Core.Caching;

/// <summary>
/// Two-age search-result cache read path (plan Step 4a, Step 2): classifies an entry into one of
/// three bands relative to <c>now</c> and decides what to serve, stamping
/// <c>LastRequestedAt</c> only when something is actually served (Architect A1, M3-8a).
///
/// This class owns band classification and the stamping/write-back rules only. Actually invoking
/// an upstream source and merging/serializing results is orchestration performed by the caller
/// (the endpoint layer); this class is handed a <paramref name="refreshTrigger"/> delegate to
/// invoke a secondary live attempt/refresh without depending on <c>IUpstreamSource</c> or the
/// circuit breaker directly, keeping it independently unit-testable against fakes.
/// </summary>
public sealed class SearchResultCache
{
    private readonly ISearchResultCacheStore _store;
    private readonly TimeProvider _timeProvider;

    public SearchResultCache(ISearchResultCacheStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Classifies band membership for a fixed <c>now</c> given an entry's stored ages, so
    /// callers/tests can reason about band boundaries without allocating an entry.
    /// </summary>
    public static CacheBand Classify(DateTimeOffset now, DateTimeOffset freshUntil, DateTimeOffset serveUntil)
    {
        if (now < freshUntil)
        {
            return CacheBand.Fresh;
        }

        return now < serveUntil ? CacheBand.StaleButValid : CacheBand.Expired;
    }

    /// <summary>
    /// Reads the entry for <paramref name="queryKey"/> and decides what to serve.
    ///
    /// - <b>Fresh</b> (<c>now &lt; FreshUntil</c>): served directly, zero upstream calls, no
    ///   refresh triggered (AC23b).
    /// - <b>Stale-but-valid</b> (<c>FreshUntil &lt;= now &lt; ServeUntil</c>): served immediately;
    ///   <paramref name="refreshTrigger"/> is invoked so a caller-supplied secondary live
    ///   attempt/refresh can run (its result, if any, is written back via
    ///   <see cref="SaveAsync"/> by the caller — this method does not await it) (AC23/AC23c).
    /// - <b>Expired</b> (<c>now &gt;= ServeUntil</c>): not served; <c>LastRequestedAt</c> is left
    ///   unchanged (M3-8a).
    ///
    /// No entry present at all is reported as <see cref="CacheBand.Expired"/> with a null payload,
    /// since nothing is servable either way.
    /// </summary>
    public async Task<CacheReadResult> GetAsync(string queryKey, Action? refreshTrigger = null, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var entry = await _store.GetAsync(queryKey, cancellationToken);

        if (entry is null)
        {
            return new CacheReadResult(CacheBand.Expired, null, null, RefreshTriggered: false);
        }

        var band = Classify(now, entry.FreshUntil, entry.ServeUntil);
        var age = now - entry.FetchedAt;

        switch (band)
        {
            case CacheBand.Fresh:
                await _store.TouchLastRequestedAsync(queryKey, now, cancellationToken);
                return new CacheReadResult(CacheBand.Fresh, entry.PayloadJson, age, RefreshTriggered: false);

            case CacheBand.StaleButValid:
                await _store.TouchLastRequestedAsync(queryKey, now, cancellationToken);
                refreshTrigger?.Invoke();
                return new CacheReadResult(CacheBand.StaleButValid, entry.PayloadJson, age, RefreshTriggered: refreshTrigger is not null);

            case CacheBand.Expired:
            default:
                // Nothing is served: LastRequestedAt MUST NOT be stamped (M3-8a).
                return new CacheReadResult(CacheBand.Expired, null, null, RefreshTriggered: false);
        }
    }

    /// <summary>
    /// Writes a freshly fetched payload for <paramref name="queryKey"/>, resetting
    /// <c>FetchedAt</c>/<c>FreshUntil</c>/<c>ServeUntil</c> from the current clock and the
    /// configured ages. Never touches <c>LastRequestedAt</c>.
    /// </summary>
    public Task SaveAsync(string queryKey, string payloadJson, TimeSpan freshUntilAge, TimeSpan serveUntilAge, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return _store.SaveAsync(queryKey, payloadJson, now, now + freshUntilAge, now + serveUntilAge, cancellationToken);
    }
}
