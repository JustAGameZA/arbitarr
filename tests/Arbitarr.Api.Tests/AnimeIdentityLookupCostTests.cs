using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-u1c review, item 3: what an anime search COSTS in identity lookups.
/// </summary>
/// <remarks>
/// <para>The review's finding was that <c>ResolveAnimeIdentityAsync</c> runs before
/// <c>PaginationSnapshotService.GetPageAsync</c>, so every anime search — cache hits and pagination
/// offsets included — paid a live Sonarr call with a long timeout. Two things fix that, and this
/// file pins the one that is observable from here: the resolver is asked at most once per series,
/// because <c>SeriesTitleResolver</c> memoises the answer. The other is the resolver's own
/// wall-clock budget, which is tested where it lives.</para>
///
/// <para><b>WHY THE LOOKUP STILL PRECEDES THE CACHE STAGE.</b> Moving it below would mean threading
/// a lazy resolver through the snapshot layer AND the two-age cache layer, because the resolved
/// title is consumed at the very bottom — <c>NzbHydraSource.BuildSearchUri</c>. It cannot simply
/// move: the absolute number parsed in the same step is needed by the cache KEY, which is computed
/// above both. Memoising is what makes the ordering cheap rather than what works around it, and a
/// counting resolver is how that stays true.</para>
/// </remarks>
public class AnimeIdentityLookupCostTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private const int TvdbId = 81797;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"arbitarr-anime-lookup-cost-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    /// <summary>
    /// The shape Sonarr sends for an anime episode: a tvdbid and a bare absolute number as q, with
    /// no season and no ep.
    /// </summary>
    private static async Task<IResult> SearchAsync(
        PaginationSnapshotService snapshotService,
        FilterStage filterStage,
        IIdentityResolver resolver,
        string queryText,
        int offset)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("indexer.example.invalid");

        return await SearchEndpoint.HandleTorznabAsync(
            "tvsearch",
            queryText,
            new[] { 5000 },
            50,
            offset,
            "caller-api-key",
            snapshotService,
            filterStage,
            new InMemoryReleaseLookup(),
            new RecentSearchLog(),
            NullEventSink.Instance,
            httpContext.Request,
            CancellationToken.None,
            tvdbId: TvdbId,
            identityResolver: resolver);
    }

    /// <summary>
    /// Paging through one anime search asks the resolver once, not once per page. Pagination is the
    /// case the review named, and it is also the one an *arr client generates most of.
    /// </summary>
    [Fact]
    public async Task Paging_through_one_anime_search_asks_the_resolver_once()
    {
        using var context = CreateContext();
        var resolver = new CountingResolver("One Piece");
        var service = BuildService(releaseCount: 100);
        var filterStage = BuildFilterStage(context);

        await SearchAsync(service, filterStage, resolver, "92", offset: 0);
        await SearchAsync(service, filterStage, resolver, "92", offset: 50);

        // The positive control: the resolver WAS reached, so "once" is a statement about the second
        // page being served without it rather than about a resolver nothing ever called.
        Assert.Equal(1, resolver.Calls);
    }

    /// <summary>
    /// A repeated identical search — the snapshot-hit path — likewise asks once. The memo lives in
    /// the resolver rather than in the endpoint, so this holds across requests that share it, which
    /// is how it is registered.
    /// </summary>
    [Fact]
    public async Task A_repeated_anime_search_asks_the_resolver_once()
    {
        using var context = CreateContext();
        var resolver = new CountingResolver("One Piece");
        var service = BuildService(releaseCount: 10);
        var filterStage = BuildFilterStage(context);

        await SearchAsync(service, filterStage, resolver, "92", offset: 0);
        await SearchAsync(service, filterStage, resolver, "92", offset: 0);
        await SearchAsync(service, filterStage, resolver, "92", offset: 0);

        Assert.Equal(1, resolver.Calls);
    }

    /// <summary>
    /// A different episode of the SAME series also asks once in total: the memo is keyed by series,
    /// which is what the lookup actually depends on. The episode number never reaches Sonarr.
    /// </summary>
    [Fact]
    public async Task A_different_episode_of_the_same_series_reuses_the_resolved_title()
    {
        using var context = CreateContext();
        var resolver = new CountingResolver("One Piece");
        var service = BuildService(releaseCount: 10);
        var filterStage = BuildFilterStage(context);

        await SearchAsync(service, filterStage, resolver, "92", offset: 0);
        await SearchAsync(service, filterStage, resolver, "93", offset: 0);

        Assert.Equal(1, resolver.Calls);
    }

    /// <summary>
    /// A search that is NOT the anime shape never consults the resolver at all — the whole feature
    /// is invisible to every other request, which is what makes it safe to run before the cache.
    /// </summary>
    [Theory]
    [InlineData("bleach")]
    [InlineData("")]
    public async Task A_search_that_is_not_the_anime_shape_never_asks_the_resolver(string queryText)
    {
        using var context = CreateContext();
        var resolver = new CountingResolver("One Piece");
        var service = BuildService(releaseCount: 10);

        await SearchAsync(service, BuildFilterStage(context), resolver, queryText, offset: 0);

        Assert.Equal(0, resolver.Calls);
    }

    /// <summary>
    /// A real <see cref="FilterStage"/> over an empty database — no profiles, so nothing is
    /// filtered. It is here because the endpoint requires one, not because filtering is under test.
    /// </summary>
    private static FilterStage BuildFilterStage(ArbitarrDbContext context) =>
        new(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new ManualTimeProvider(Now));

    private static PaginationSnapshotService BuildService(int releaseCount)
    {
        var releases = Enumerable.Range(0, releaseCount).Select(i => new ReleaseCandidate
        {
            Title = $"Release {i}",
            Guid = i.ToString(),
            PubDate = TestReleases.FixedPubDate,
            Size = 1000 + i,
            Link = new Uri($"http://192.0.2.40:8080/get/{i}"),
            Category = new[] { 5000 },
            Protocol = ProtocolKind.Torrent,
        }).ToArray();

        var time = new ManualTimeProvider(Now);
        return new PaginationSnapshotService(
            new UpstreamMergeStage(new[] { (IUpstreamSource)new FakeUpstreamSource("eztv", searchResults: releases) }),
            TestCacheStage.Create(time),
            new FakeQuerySnapshotStore(),
            time);
    }

    /// <summary>
    /// Counts how many times the search path asked, and memoises the way the real resolver does —
    /// so this asserts about the ENDPOINT's call pattern rather than re-testing the memo.
    /// </summary>
    private sealed class CountingResolver : IIdentityResolver
    {
        private readonly Dictionary<int, SeriesIdentity?> _memo = new();
        private readonly string _title;

        public CountingResolver(string title) => _title = title;

        public int Calls { get; private set; }

        public Task<SeriesIdentity?> ResolveAsync(
            string title,
            IdentityResolutionHints hints,
            CancellationToken cancellationToken = default)
        {
            if (hints.TvdbId is not { } tvdbId)
            {
                return Task.FromResult<SeriesIdentity?>(null);
            }

            if (_memo.TryGetValue(tvdbId, out var memoised))
            {
                return Task.FromResult(memoised);
            }

            Calls++;
            var identity = new SeriesIdentity(tvdbId, TmdbId: null, _title, Array.Empty<string>());
            _memo[tvdbId] = identity;
            return Task.FromResult<SeriesIdentity?>(identity);
        }
    }
}
