using Arbitarr.Core.Caching;

namespace Arbitarr.Api.Tests;

/// <summary>
/// In-memory <see cref="ISearchResultCacheStore"/> for exercising <see cref="Arbitarr.Api.Search.SearchResultCacheStage"/>
/// (and, through it, <see cref="Arbitarr.Api.Search.PaginationSnapshotService"/>) without a real database. Mirrors the
/// fake in Arbitarr.Core.Tests/SearchResultCacheBandTests.cs so both suites exercise identical store semantics.
/// </summary>
internal sealed class FakeSearchResultCacheStore : ISearchResultCacheStore
{
    private readonly Dictionary<string, CachedSearchResult> _entries = new();

    public Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default)
        => Task.FromResult(_entries.TryGetValue(queryKey, out var entry) ? entry : null);

    public Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default)
    {
        var previousStamp = _entries.TryGetValue(queryKey, out var existing) ? existing.LastRequestedAt : default;
        _entries[queryKey] = new CachedSearchResult(queryKey, payloadJson, fetchedAt, freshUntil, serveUntil, previousStamp);
        return Task.CompletedTask;
    }

    public Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(queryKey, out var entry))
        {
            _entries[queryKey] = entry with { LastRequestedAt = requestedAt };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CachedSearchResult>>(Array.Empty<CachedSearchResult>());
}
