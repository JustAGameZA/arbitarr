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
        CreateWithStore(timeProvider).Stage;

    /// <summary>
    /// arb-apm8: the same wiring, but handing back the backing store as well, so a test can assert
    /// what the two-age cache DID or DID NOT receive. <see cref="Create(TimeProvider)"/> hides the
    /// store, which is right for tests that only care about the snapshot layer, but a test about
    /// whether a degraded fetch was written as fresh has to be able to look at the row itself —
    /// asserting only on the returned band would pass against an implementation that stored the
    /// empty set and merely labelled it Expired.
    /// </summary>
    public static (SearchResultCacheStage Stage, FakeSearchResultCacheStore Store) CreateWithStore(TimeProvider timeProvider)
    {
        var store = new FakeSearchResultCacheStore();
        return (new SearchResultCacheStage(new SearchResultCache(store, timeProvider)), store);
    }
}
