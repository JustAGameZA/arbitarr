using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { source }));
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

    // ---- arb-b5z: the source set is part of what a snapshot IS ---------------------------------
    //
    // These three tests are written ACROSS A RESTART rather than around an in-process mutation, and
    // that shape is the whole point. The resolved source configuration is settled once at startup
    // (SourceSeeder writes ResolvedSourceConfiguration before the first request), so a source edit
    // requires a restart and the fingerprint cannot move within one process. What makes the bug real
    // is that the snapshot store is SQLite-backed and OUTLIVES that restart. A test that mutated a
    // source and re-queried the same service would therefore pass for the wrong reason — or not at
    // all — while proving nothing about the failure operators actually hit.
    //
    // Each test builds TWO services over ONE store, which is exactly "the same database, a new
    // process".

    /// <summary>
    /// A restart that CHANGES the source set must not serve the pre-restart snapshot: the operator
    /// added or enabled a source precisely so its releases would appear, and the stale row would
    /// hide them for the remainder of its TTL with nothing to indicate why.
    /// </summary>
    [Fact]
    public async Task A_restart_with_a_changed_source_set_materializes_a_new_snapshot()
    {
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var query = new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0);

        await BuildService(store, time, "eztv").GetPageAsync("search", query);
        await BuildService(store, time, "eztv,nyaa").GetPageAsync("search", query);

        Assert.Equal(2, store.SaveCallCount);
    }

    /// <summary>
    /// The positive control, and the half that keeps the test above honest: a restart that leaves
    /// the source set ALONE must still hit the surviving snapshot. Without this, "two saves" would
    /// pass just as happily for a token that had stopped collapsing anything at all — which would
    /// silently turn every restart into a full re-materialization.
    /// </summary>
    [Fact]
    public async Task A_restart_with_an_unchanged_source_set_still_serves_the_surviving_snapshot()
    {
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var query = new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0);

        await BuildService(store, time, "eztv").GetPageAsync("search", query);
        await BuildService(store, time, "eztv").GetPageAsync("search", query);

        Assert.Equal(1, store.SaveCallCount);
    }

    /// <summary>
    /// The no-regression case: a caller that says nothing about a source set — which is every
    /// pre-arb-b5z construction site — must produce the token it always did. Asserted as behaviour
    /// (two differently-built services sharing one snapshot) rather than by re-deriving the hash,
    /// which would only restate the implementation.
    /// </summary>
    [Fact]
    public async Task A_service_with_no_fingerprint_source_keeps_the_token_it_had()
    {
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var query = new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0);

        // The pre-existing five-argument constructor, then the same thing said explicitly.
        var implicitDefault = new PaginationSnapshotService(
            new UpstreamMergeStage(new StaticSourceRegistry(new[] { MakeSourceWithReleases("eztv", 10) })),
            TestCacheStage.Create(time),
            store,
            time);
        var explicitEmpty = BuildService(store, time, string.Empty);

        await implicitDefault.GetPageAsync("search", query);
        await explicitEmpty.GetPageAsync("search", query);

        Assert.Equal(1, store.SaveCallCount);
    }

    /// <summary>
    /// One "process": a service over the shared <paramref name="store"/> whose source set is
    /// described by <paramref name="fingerprint"/>.
    /// </summary>
    private static PaginationSnapshotService BuildService(
        FakeQuerySnapshotStore store,
        ManualTimeProvider time,
        string fingerprint) =>
        new(
            new UpstreamMergeStage(new StaticSourceRegistry(new[] { MakeSourceWithReleases("eztv", 10) })),
            TestCacheStage.Create(time),
            store,
            time,
            ttl: null,
            sourceSetFingerprintSource: new StaticSourceSetFingerprintSource(fingerprint));

    /// <summary>
    /// arb-apm8: a source whose own budget fired. <see cref="UpstreamMergeStage"/> classifies an
    /// <see cref="OperationCanceledException"/> as a TIMEOUT only when the caller's token is not
    /// cancelled AND the exception's token is not the caller's, so the token here is a foreign one
    /// that is never handed to the merge — which is exactly the shape a linked HttpClient.Timeout
    /// produces. A plain <c>new OperationCanceledException()</c> would carry <c>CancellationToken.None</c>
    /// and be classified as a generic failure instead, quietly testing the wrong list.
    /// </summary>
    private static IUpstreamSource MakeTimedOutSource(string name) =>
        new FakeUpstreamSource(name, searchException: new OperationCanceledException(new CancellationTokenSource().Token));

    private static IUpstreamSource MakeFailedSource(string name) =>
        new FakeUpstreamSource(name, searchException: new HttpRequestException("connection refused"));

    /// <summary>
    /// <b>NON-VACUITY GUARD for the tests below</b> (CLAUDE.md §4). Each of them asserts that
    /// nothing was written, and an assertion of that shape passes just as happily against a merge
    /// that never reached the classifier at all — a source that threw the wrong exception type, or
    /// a fake whose exception never escaped, would land in the WRONG list (or in none) and still
    /// produce "no rows written". This proves the source really is in the list the test is about
    /// before any absence is asserted.
    /// </summary>
    [Fact]
    public async Task The_timeout_and_failure_fakes_really_do_land_in_their_own_merge_result_lists()
    {
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(
            new[] { MakeTimedOutSource("eztv"), MakeFailedSource("nzbgeek") }));

        var merged = await mergeStage.MergeAsync(new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0));

        Assert.Equal(new[] { "eztv" }, merged.TimedOutSources);
        Assert.Equal(new[] { "nzbgeek" }, merged.FailedSources);
        Assert.Empty(merged.RateLimitedSources);
        Assert.Empty(merged.Releases);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL.</b> A clean merge DOES write a snapshot and DOES populate the two-age
    /// cache as fresh. Without this, the tests below would hold just as well against a service
    /// that had stopped writing anything at all.
    /// </summary>
    [Fact]
    public async Task A_clean_merge_writes_a_snapshot_and_populates_the_two_age_cache_as_fresh()
    {
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { MakeSourceWithReleases("eztv", 3) }));
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var (cacheStage, cacheStore) = TestCacheStage.CreateWithStore(time);
        var service = new PaginationSnapshotService(mergeStage, cacheStage, store, time);
        var query = new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0);

        var result = await service.GetPageAsync("search", query);

        Assert.Equal(3, result.Releases.Count);
        Assert.Equal(CacheBand.Fresh, result.CacheBand);
        Assert.Equal(1, store.SaveCallCount);
        Assert.NotNull(await cacheStore.GetAsync(SearchResultCacheStage.BuildQueryKey(query)));
    }

    /// <summary>
    /// <b>THE POINT OF arb-apm8.</b> A merge in which the only failure is a TIMEOUT writes neither a
    /// snapshot nor a two-age cache row. Before this bead the Degraded flag was computed from
    /// RateLimitedSources alone, so this merge reported Degraded=false and its empty set was stored
    /// as a legitimate fresh answer for the whole FreshUntil/ServeUntil band.
    ///
    /// <para>The cache row is asserted directly rather than via the returned band: an implementation
    /// that stored the empty set and merely LABELLED the response Expired would pass a band-only
    /// assertion while leaving exactly the row this bead exists to prevent.</para>
    ///
    /// <para>TimedOutSources is populated by more than one mechanism, and this test covers all of
    /// them because it asserts on the LIST rather than on what filled it. Today it is a source
    /// exceeding its own budget; since arb-4cso it is also a source that was healthy but slower than
    /// the whole-fan-out pass ceiling. Both mean the same thing here — that source contributed
    /// nothing and said nothing — so both must keep the empty set out of the cache.</para>
    /// </summary>
    [Fact]
    public async Task A_fully_timed_out_merge_writes_no_snapshot_and_does_not_populate_the_cache_as_fresh()
    {
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { MakeTimedOutSource("eztv") }));
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var (cacheStage, cacheStore) = TestCacheStage.CreateWithStore(time);
        var service = new PaginationSnapshotService(mergeStage, cacheStage, store, time);
        var query = new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0);

        var result = await service.GetPageAsync("search", query);

        // The source really did time out on THIS run, so the absences below are about a merge that
        // reached the classifier rather than one that silently did nothing.
        Assert.Equal(new[] { "eztv" }, result.TimedOutSources);
        Assert.Empty(result.Releases);

        Assert.Equal(0, store.SaveCallCount);
        Assert.Null(await cacheStore.GetAsync(SearchResultCacheStage.BuildQueryKey(query)));
        Assert.Equal(CacheBand.Expired, result.CacheBand);
    }

    /// <summary>The same for a transport/protocol/parse failure, which is the other list arb-apm8 adds.</summary>
    [Fact]
    public async Task A_fully_failed_merge_writes_no_snapshot_and_does_not_populate_the_cache_as_fresh()
    {
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { MakeFailedSource("eztv") }));
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var (cacheStage, cacheStore) = TestCacheStage.CreateWithStore(time);
        var service = new PaginationSnapshotService(mergeStage, cacheStage, store, time);
        var query = new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0);

        var result = await service.GetPageAsync("search", query);

        Assert.Equal(new[] { "eztv" }, result.FailedSources);
        Assert.Empty(result.Releases);

        Assert.Equal(0, store.SaveCallCount);
        Assert.Null(await cacheStore.GetAsync(SearchResultCacheStage.BuildQueryKey(query)));
        Assert.Equal(CacheBand.Expired, result.CacheBand);
    }

    /// <summary>
    /// A PARTIAL degradation is unchanged: one source timed out, another returned releases, so the
    /// set is trustworthy and is both snapshotted and cached. This is what keeps the fix from
    /// over-reaching into "any failure anywhere suppresses caching".
    /// </summary>
    [Fact]
    public async Task A_merge_with_one_timed_out_source_and_one_healthy_source_is_still_snapshotted_and_cached()
    {
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(
            new[] { MakeTimedOutSource("eztv"), MakeSourceWithReleases("nzbgeek", 2) }));
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var (cacheStage, cacheStore) = TestCacheStage.CreateWithStore(time);
        var service = new PaginationSnapshotService(mergeStage, cacheStage, store, time);
        var query = new SearchQuery("x", Array.Empty<int>(), 5, SearchProtocol.Torznab, 0);

        var result = await service.GetPageAsync("search", query);

        Assert.Equal(new[] { "eztv" }, result.TimedOutSources);
        Assert.Equal(2, result.Releases.Count);
        Assert.Equal(1, store.SaveCallCount);
        Assert.NotNull(await cacheStore.GetAsync(SearchResultCacheStage.BuildQueryKey(query)));
    }
}
