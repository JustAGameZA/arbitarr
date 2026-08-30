using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Test-only factory for a <see cref="SearchResultCacheStage"/> backed by an in-memory
/// <see cref="FakeSearchResultCacheStore"/>, so <see cref="PaginationSnapshotService"/> tests that
/// don't care about two-age cache behavior itself can wire one up in a single line.
/// </summary>
internal static class TestCacheStage
{
    public static SearchResultCacheStage Create(TimeProvider timeProvider) =>
        new(new SearchResultCache(new FakeSearchResultCacheStore(), timeProvider));
}
