using Arbitarr.Core.Caching;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Caching;

/// <summary>
/// EF Core-backed <see cref="ISearchResultCacheStore"/> against <see cref="SearchResultCacheEntry"/>,
/// following the same thin-adapter pattern as
/// <see cref="Arbitarr.Data.CircuitBreaker.SourceHealthRepository"/>: the two-age cache's read/write
/// rules (<see cref="SearchResultCache"/>) stay persistence-agnostic in Core, and this class only
/// translates to/from the EF entity (AC6).
/// </summary>
public sealed class SearchResultCacheStore : ISearchResultCacheStore
{
    private readonly ArbitarrDbContext _dbContext;

    public SearchResultCacheStore(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.SearchResultCacheEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.QueryKey == queryKey, cancellationToken);

        return record is null ? null : ToCachedSearchResult(record);
    }

    public async Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.SearchResultCacheEntries
            .SingleOrDefaultAsync(e => e.QueryKey == queryKey, cancellationToken);

        if (record is null)
        {
            // New entry: LastRequestedAt starts unset (default) until a caller actually serves it.
            record = new SearchResultCacheEntry { QueryKey = queryKey, PayloadJson = payloadJson };
            _dbContext.SearchResultCacheEntries.Add(record);
        }

        // SaveAsync never touches LastRequestedAt (M3-8a) — only the fields below are updated.
        record.PayloadJson = payloadJson;
        record.FetchedAt = fetchedAt;
        record.FreshUntil = freshUntil;
        record.ServeUntil = serveUntil;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.SearchResultCacheEntries
            .SingleOrDefaultAsync(e => e.QueryKey == queryKey, cancellationToken);

        if (record is null)
        {
            return;
        }

        record.LastRequestedAt = requestedAt;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
    {
        // As with MaintenanceJob's prune predicates, SQLite's EF Core provider cannot reliably
        // translate DateTimeOffset arithmetic/comparisons server-side, so candidates are filtered
        // client-side against the selection predicate documented on
        // ISearchResultCacheStore.GetRefreshCandidatesAsync, rather than re-expressing it as a
        // (potentially divergent) SQL WHERE clause.
        // TODO(M7 observability): this scan is O(rows) over the whole table every worker cycle.
        // Revisit with an index-assisted or paginated approach if the table grows large enough for
        // this to show up in M7's soak/synthetic-ageing metrics.
        var candidates = await _dbContext.SearchResultCacheEntries
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var activeSince = now - activeWindow;
        var refreshThreshold = now + refreshLead;

        var records = candidates
            .Where(e => e.LastRequestedAt > activeSince && refreshThreshold >= e.FreshUntil)
            .ToList();

        return records.Select(ToCachedSearchResult).ToList();
    }

    private static CachedSearchResult ToCachedSearchResult(SearchResultCacheEntry record) => new(
        QueryKey: record.QueryKey,
        PayloadJson: record.PayloadJson,
        FetchedAt: record.FetchedAt,
        FreshUntil: record.FreshUntil,
        ServeUntil: record.ServeUntil,
        LastRequestedAt: record.LastRequestedAt);
}
