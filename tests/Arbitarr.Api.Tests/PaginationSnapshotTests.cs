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

        var firstPage = await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 50, SearchProtocol.Torznab, 0));
        var secondPage = await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 50, SearchProtocol.Torznab, 50));

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

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0));
        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 5));

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

        await service.GetPageAsync("search", new SearchQuery("bleach", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0));
        await service.GetPageAsync("search", new SearchQuery("naruto", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0));

        Assert.Equal(2, store.SaveCallCount);
    }

    [Theory]
    [InlineData(110382, 12345, 18, 5)]
    [InlineData(110381, 12346, 18, 5)]
    [InlineData(110381, 12345, 19, 5)]
    [InlineData(110381, 12345, 18, 6)]
    public async Task Different_tv_episode_identity_component_produces_a_different_snapshot(
        int tvdbId,
        int tmdbId,
        int season,
        int episode)
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        await service.GetPageAsync("tvsearch", new SearchQuery("", new[] { 5000 }, 5, SearchProtocol.Torznab, TvdbId: 110381, TmdbId: 12345, Season: 18, Episode: 5));
        await service.GetPageAsync("tvsearch", new SearchQuery("", new[] { 5000 }, 5, SearchProtocol.Torznab, TvdbId: tvdbId, TmdbId: tmdbId, Season: season, Episode: episode));

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

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0));
        Assert.Equal(1, store.SaveCallCount);

        time.Advance(TimeSpan.FromSeconds(61));

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0));
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

        var result = await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0));

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

        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 3, SearchProtocol.Torznab, 0));
        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 7, SearchProtocol.Torznab, 3));
        await service.GetPageAsync("search", new SearchQuery("x", Array.Empty<int>(), 1, SearchProtocol.Torznab, 9));

        // All three requests share the same query identity (text/categories), differing only in
        // offset/limit — they must all resolve to the same materialized snapshot.
        Assert.Equal(1, store.SaveCallCount);
    }

    /// <summary>
    /// arb-u1c review. The snapshot is checked BEFORE the two-age cache and returns without ever
    /// reaching it, so a separation that exists only in the two-age key is unreachable for any query
    /// a live snapshot covers. The resolved title changes the upstream URL — that is its entire
    /// purpose — so two requests differing only by it resolve to different result sets and must not
    /// share a snapshot.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is concrete and self-inflicted: the first anime search issued
    /// before Sonarr is configured resolves no title, materialises an id-only result set, and — with
    /// the title absent from the token — every correctly resolved search for that episode reads that
    /// set back for the snapshot's whole TTL. Configuring Sonarr would appear to do nothing.
    /// </remarks>
    [Fact]
    public async Task A_resolved_title_produces_a_different_snapshot_than_the_unresolved_request()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        var unresolved = new SearchQuery(
            "92", new[] { 5000 }, 5, SearchProtocol.Torznab, 0,
            TvdbId: 81797, Type: SearchType.TvSearch, Absolute: 92);

        await service.GetPageAsync("tvsearch", unresolved);
        await service.GetPageAsync("tvsearch", unresolved with { ResolvedTitle = "One Piece" });

        Assert.Equal(2, store.SaveCallCount);
    }

    /// <summary>
    /// The positive control for the test above: the very same query, repeated unchanged, must still
    /// collapse onto ONE snapshot. Without this, "two saves" would also pass for a token that had
    /// simply stopped working.
    /// </summary>
    [Fact]
    public async Task An_unchanged_anime_query_still_shares_one_snapshot()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        var resolved = new SearchQuery(
            "92", new[] { 5000 }, 5, SearchProtocol.Torznab, 0,
            TvdbId: 81797, Type: SearchType.TvSearch, Absolute: 92, ResolvedTitle: "One Piece");

        await service.GetPageAsync("tvsearch", resolved);
        await service.GetPageAsync("tvsearch", resolved with { Offset = 5 });

        Assert.Equal(1, store.SaveCallCount);
    }

    /// <summary>
    /// The absolute number is a token component too. It is belt-and-braces today — the number is
    /// derived from the query text, which is already hashed — but the two-age key deliberately does
    /// NOT depend on that derivation, so a future caller setting the number from a real numbering
    /// resolver rather than from the text would otherwise share one snapshot between episodes.
    /// </summary>
    [Fact]
    public async Task A_different_absolute_episode_produces_a_different_snapshot()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        var episode = new SearchQuery(
            "", new[] { 5000 }, 5, SearchProtocol.Torznab, 0,
            TvdbId: 81797, Type: SearchType.TvSearch, Absolute: 91);

        await service.GetPageAsync("tvsearch", episode);
        await service.GetPageAsync("tvsearch", episode with { Absolute = 92 });

        Assert.Equal(2, store.SaveCallCount);
    }

    /// <summary>
    /// The no-regression guard: every pre-existing construction site leaves both new components
    /// unset, so they must be inert for those queries rather than merely compatible in principle.
    /// </summary>
    [Fact]
    public async Task Queries_that_set_neither_new_component_keep_the_snapshot_they_had()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);

        var seasonal = new SearchQuery(
            "Bleach", new[] { 5000 }, 5, SearchProtocol.Torznab, 0,
            TvdbId: 74796, Season: 17, Episode: 36);

        await service.GetPageAsync("tvsearch", seasonal);
        await service.GetPageAsync("tvsearch", seasonal with { Absolute = null, ResolvedTitle = null });

        Assert.Equal(1, store.SaveCallCount);
    }
}
