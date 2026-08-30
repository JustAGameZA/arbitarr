using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Exercises <see cref="PaginationSnapshotService"/>: snapshot-hit vs snapshot-miss behavior,
/// offset/limit slicing, the AC16 disjoint-union-complete-across-pages guarantee, TTL expiry,
/// and the deliberate no-cache-on-full-rate-limit behavior (M1-5/AC16).
/// </summary>
public class PaginationSnapshotTests
{
    private static IUpstreamSource MakeSourceWithReleases(string name, int count)
    {
        var releases = Enumerable.Range(0, count).Select(i => new ReleaseCandidate
        {
            Title = $"Release {i}",
            Guid = i.ToString(),
            PubDate = TestReleases.FixedPubDate,
            Size = 1000 + i,
            Link = new Uri($"http://192.0.2.40:8080/get/{i}"),
            Category = new[] { 5000 },
            Protocol = ProtocolKind.Torrent,
        }).ToArray();

        return new FakeUpstreamSource(name, searchResults: releases);
    }

    [Fact]
    public async Task Offset_0_then_offset_50_over_100_items_yields_a_disjoint_union_complete_pair_of_pages()
    {
        var source = MakeSourceWithReleases("eztv", 100);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        var firstPage = await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 50, 0));
        var secondPage = await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 50, 50));

        Assert.Equal(50, firstPage.Releases.Count);
        Assert.Equal(50, secondPage.Releases.Count);

        var firstGuids = firstPage.Releases.Select(r => r.Candidate.Guid).ToHashSet();
        var secondGuids = secondPage.Releases.Select(r => r.Candidate.Guid).ToHashSet();

        Assert.Empty(firstGuids.Intersect(secondGuids));
        Assert.Equal(100, firstGuids.Union(secondGuids).Count());
    }

    [Fact]
    public async Task Second_page_request_for_the_same_query_hits_the_snapshot_and_does_not_re_merge()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, 0));
        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, 5));

        // Only the first (cache-miss) call should have persisted a snapshot.
        Assert.Equal(1, store.SaveCallCount);
    }

    [Fact]
    public async Task Different_query_text_produces_a_different_snapshot_and_triggers_a_fresh_merge()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        await service.GetPageAsync("search", new SearchQuery("bleach", Array.Empty<int>(), 5, 0));
        await service.GetPageAsync("search", new SearchQuery("naruto", Array.Empty<int>(), 5, 0));

        Assert.Equal(2, store.SaveCallCount);
    }

    [Fact]
    public async Task Expired_snapshot_triggers_a_fresh_merge_instead_of_serving_stale_data()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time, ttl: TimeSpan.FromSeconds(60));

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, 0));
        Assert.Equal(1, store.SaveCallCount);

        time.Advance(TimeSpan.FromSeconds(61));

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, 0));
        Assert.Equal(2, store.SaveCallCount);
    }

    [Fact]
    public async Task Fully_rate_limited_merge_with_zero_results_is_not_cached()
    {
        var source = new FakeUpstreamSource("eztv", searchException: new RequestLimitReachedException("eztv"));
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        var result = await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, 0));

        Assert.Empty(result.Releases);
        Assert.Contains("eztv", result.RateLimitedSources);
        Assert.Equal(0, store.SaveCallCount);
    }

    [Fact]
    public async Task Offset_and_limit_do_not_affect_the_snapshot_token()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 3, 0));
        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 7, 3));
        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 1, 9));

        // All three requests share the same query identity (text/categories), differing only in
        // offset/limit — they must all resolve to the same materialized snapshot.
        Assert.Equal(1, store.SaveCallCount);
    }
}
