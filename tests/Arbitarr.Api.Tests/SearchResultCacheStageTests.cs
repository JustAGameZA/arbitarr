using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Exercises <see cref="SearchResultCacheStage"/>: the band-driven serve/fetch decisions (M3-8),
/// the "a degraded, empty fetch never overwrites stored data" rule (M3-10), the stale-but-valid
/// refresh trigger (M3-11), and the id-based key collapse across differing query text (M3-9).
/// </summary>
public class SearchResultCacheStageTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static RenderedRelease MakeRelease(string guid) =>
        new("eztv", new ReleaseCandidate
        {
            Title = $"Release {guid}",
            Guid = guid,
            PubDate = TestReleases.FixedPubDate,
            Size = 1000,
            Link = new Uri($"http://192.0.2.40:8080/get/{guid}"),
            Category = new[] { 5000 },
            Protocol = ProtocolKind.Torrent,
        });

    private static (SearchResultCacheStage Stage, FakeSearchResultCacheStore Store, SearchResultCache Cache) Build(ManualTimeProvider time)
    {
        var store = new FakeSearchResultCacheStore();
        var cache = new SearchResultCache(store, time);
        return (new SearchResultCacheStage(cache), store, cache);
    }

    private static Func<CancellationToken, Task<UpstreamFetchResult>> Fetch(
        IReadOnlyList<RenderedRelease> releases,
        bool degraded,
        Action? onCall = null) =>
        _ =>
        {
            onCall?.Invoke();
            return Task.FromResult(new UpstreamFetchResult(releases, degraded));
        };

    [Fact]
    public async Task Cache_miss_fetches_upstream_once_stores_the_result_and_serves_it_as_fresh()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, store, _) = Build(time);
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);
        var calls = 0;

        var result = await stage.GetAsync(query, Fetch(new[] { MakeRelease("a") }, degraded: false, onCall: () => calls++));

        Assert.Equal(1, calls);
        Assert.Equal(CacheBand.Fresh, result.Band);
        Assert.Equal(TimeSpan.Zero, result.Age);
        Assert.Single(result.Releases);

        var stored = await store.GetAsync(SearchResultCacheStage.BuildQueryKey(query));
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task Degraded_and_empty_fetch_stores_nothing_and_returns_an_expired_empty_set()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, store, _) = Build(time);
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);

        var result = await stage.GetAsync(query, Fetch(Array.Empty<RenderedRelease>(), degraded: true));

        Assert.Empty(result.Releases);
        Assert.Equal(CacheBand.Expired, result.Band);
        Assert.Null(result.Age);
        Assert.Null(await store.GetAsync(SearchResultCacheStage.BuildQueryKey(query)));
    }

    [Fact]
    public async Task Degraded_but_non_empty_fetch_is_still_stored_and_served_as_fresh()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, store, _) = Build(time);
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);

        var result = await stage.GetAsync(query, Fetch(new[] { MakeRelease("a") }, degraded: true));

        Assert.Equal(CacheBand.Fresh, result.Band);
        Assert.NotNull(await store.GetAsync(SearchResultCacheStage.BuildQueryKey(query)));
    }

    [Fact]
    public async Task Fresh_hit_is_served_from_cache_with_zero_upstream_calls()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, _, _) = Build(time);
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);
        var calls = 0;

        await stage.GetAsync(query, Fetch(new[] { MakeRelease("a") }, degraded: false, onCall: () => calls++));
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await stage.GetAsync(query, Fetch(new[] { MakeRelease("b") }, degraded: false, onCall: () => calls++));

        Assert.Equal(1, calls);
        Assert.Equal(CacheBand.Fresh, second.Band);
        Assert.Equal("a", Assert.Single(second.Releases).Candidate.Guid);
    }

    [Fact]
    public async Task Stale_but_valid_hit_is_served_from_cache_and_invokes_the_refresh_trigger()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, _, _) = Build(time);
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);
        var calls = 0;

        await stage.GetAsync(query, Fetch(new[] { MakeRelease("a") }, degraded: false, onCall: () => calls++));

        // Past FreshUntil but well inside ServeUntil: the stale-but-valid band.
        time.Advance(RefreshWorkerDefaults.FreshUntilAge + TimeSpan.FromMinutes(1));

        var triggered = false;
        var second = await stage.GetAsync(
            query,
            Fetch(new[] { MakeRelease("b") }, degraded: false, onCall: () => calls++),
            refreshTrigger: () => triggered = true);

        Assert.Equal(1, calls);
        Assert.True(triggered);
        Assert.Equal(CacheBand.StaleButValid, second.Band);
        Assert.Equal("a", Assert.Single(second.Releases).Candidate.Guid);
    }

    [Fact]
    public async Task Expired_entry_is_not_served_and_triggers_a_fresh_upstream_fetch()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, _, _) = Build(time);
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);
        var calls = 0;

        await stage.GetAsync(query, Fetch(new[] { MakeRelease("a") }, degraded: false, onCall: () => calls++));
        time.Advance(RefreshWorkerDefaults.ServeUntilAge + TimeSpan.FromMinutes(1));
        var second = await stage.GetAsync(query, Fetch(new[] { MakeRelease("b") }, degraded: false, onCall: () => calls++));

        Assert.Equal(2, calls);
        Assert.Equal(CacheBand.Fresh, second.Band);
        Assert.Equal("b", Assert.Single(second.Releases).Candidate.Guid);
    }

    [Fact]
    public async Task Id_based_queries_with_different_query_text_collapse_onto_one_cache_entry()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, _, _) = Build(time);
        var calls = 0;

        // Same tvdbid/season/ep/categories, different free-text spellings of the same episode (M3-9).
        var first = new SearchQuery("Bleach S17E36", new[] { 5000 }, 50, 0, TvdbId: 74796, Season: 17, Episode: 36);
        var second = new SearchQuery("Bleach 17x36", new[] { 5000 }, 50, 0, TvdbId: 74796, Season: 17, Episode: 36);

        Assert.Equal(SearchResultCacheStage.BuildQueryKey(first), SearchResultCacheStage.BuildQueryKey(second));

        await stage.GetAsync(first, Fetch(new[] { MakeRelease("a") }, degraded: false, onCall: () => calls++));
        var served = await stage.GetAsync(second, Fetch(new[] { MakeRelease("b") }, degraded: false, onCall: () => calls++));

        Assert.Equal(1, calls);
        Assert.Equal(CacheBand.Fresh, served.Band);
        Assert.Equal("a", Assert.Single(served.Releases).Candidate.Guid);
    }

    [Fact]
    public async Task Query_text_only_searches_with_different_text_do_not_share_a_cache_entry()
    {
        var time = new ManualTimeProvider(Start);
        var (stage, _, _) = Build(time);
        var calls = 0;

        var first = new SearchQuery("bleach", Array.Empty<int>(), 50);
        var second = new SearchQuery("naruto", Array.Empty<int>(), 50);

        Assert.NotEqual(SearchResultCacheStage.BuildQueryKey(first), SearchResultCacheStage.BuildQueryKey(second));

        await stage.GetAsync(first, Fetch(new[] { MakeRelease("a") }, degraded: false, onCall: () => calls++));
        await stage.GetAsync(second, Fetch(new[] { MakeRelease("b") }, degraded: false, onCall: () => calls++));

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Very_long_query_text_still_yields_a_bounded_cache_key()
    {
        // Security-m3 HIGH #1: an unbounded q-derived fallback text must not itself carry unbounded
        // length into the identity/key path, independent of SearchCacheKeyBuilder's own hashing.
        var query = new SearchQuery(new string('x', 10_000), Array.Empty<int>(), 50);

        var key = SearchResultCacheStage.BuildQueryKey(query);

        Assert.True(key.Length < 200, $"Expected a bounded key under 200 chars, got {key.Length}.");
    }

    [Fact]
    public void Query_text_beyond_the_bound_still_collapses_with_an_identical_prefix()
    {
        // Two texts that agree within the 256-char cap must still collapse onto one key even though
        // their full raw text differs beyond that point.
        var prefix = new string('x', 256);
        var first = new SearchQuery(prefix + "-tail-one", Array.Empty<int>(), 50);
        var second = new SearchQuery(prefix + "-tail-two", Array.Empty<int>(), 50);

        Assert.Equal(SearchResultCacheStage.BuildQueryKey(first), SearchResultCacheStage.BuildQueryKey(second));
    }

    [Fact]
    public void Categories_are_part_of_the_cache_key()
    {
        var withCategory = new SearchQuery("bleach", new[] { 5000 }, 50, 0, TvdbId: 74796, Season: 17, Episode: 36);
        var withoutCategory = new SearchQuery("bleach", Array.Empty<int>(), 50, 0, TvdbId: 74796, Season: 17, Episode: 36);

        Assert.NotEqual(
            SearchResultCacheStage.BuildQueryKey(withCategory),
            SearchResultCacheStage.BuildQueryKey(withoutCategory));
    }
}
