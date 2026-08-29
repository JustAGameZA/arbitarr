namespace Arbitarr.Core.Caching;

/// <summary>
/// Persistence-agnostic store for search-result cache entries, keyed by resolved-identity
/// <c>QueryKey</c> (AC23b(4)/M3-9). The EF Core-backed implementation against
/// <c>Arbitarr.Data.Entities.SearchResultCacheEntry</c> is wired up separately, outside Core, to
/// keep Core free of references to Arbitarr.Data (AC6).
/// </summary>
public interface ISearchResultCacheStore
{
    /// <summary>Retrieves the entry for <paramref name="queryKey"/>, if one exists.</summary>
    Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the entry for <paramref name="queryKey"/> with a freshly fetched payload, resetting
    /// <c>FetchedAt</c>/<c>FreshUntil</c>/<c>ServeUntil</c>. Does not touch <c>LastRequestedAt</c>.
    /// </summary>
    Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps <c>LastRequestedAt</c> for <paramref name="queryKey"/> to <paramref name="requestedAt"/>.
    /// Callers must invoke this only when an entry is actually served (Fresh or Stale-but-valid
    /// band) — never for an Expired-band request, which serves nothing (M3-8a).
    /// </summary>
    Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns entries eligible for proactive refresh per the worker's selection predicate:
    /// <c>LastRequestedAt &gt; now - activeWindow AND now &gt;= FreshUntil - refreshLead</c> (Step 3).
    /// </summary>
    Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default);
}
