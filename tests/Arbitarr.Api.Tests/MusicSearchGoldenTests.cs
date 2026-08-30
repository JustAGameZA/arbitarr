using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Exercises <c>t=music</c> (M1-11, AC2) end-to-end through <see cref="SearchEndpoint"/>: AC2
/// names <c>music</c> alongside <c>search</c>/<c>tvsearch</c>/<c>movie</c>, but no real upstream
/// music fixture exists under docs/fixtures/nzbhydra/ (the live capture only returned torrent
/// TV/movie/anime results — see that directory's README "Notes and honest gaps"). This release is
/// therefore a hand-authored, clearly-labelled-synthetic Usenet/audio release rather than a
/// captured upstream payload; the rendering pipeline itself is protocol-shape-driven (not
/// query-type-driven), so this still exercises the real code path end-to-end without silently
/// skipping the AC2 requirement.
/// </summary>
public class MusicSearchGoldenTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-music-golden-test-{Guid.NewGuid():N}.db");

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


    // SYNTHETIC fixture — hand-authored, not captured from a live upstream (see class remarks).
    private static ReleaseCandidate SyntheticMusicRelease() => new()
    {
        Title = "Synthetic Artist - Synthetic Album (2026) [FLAC]",
        Guid = "synthetic-music-1",
        PubDate = TestReleases.FixedPubDate,
        Size = 450_000_000,
        Link = new Uri("http://192.0.2.50:8080/getnzb/synthetic-music-1"),
        Category = new[] { 3010 },
        Protocol = ProtocolKind.Usenet,
    };

    [Fact]
    public async Task T_music_renders_search_results_through_the_torznab_wrapper()
    {
        using var context = CreateContext();
        var source = new FakeUpstreamSource("synthsrc", searchResults: new[] { SyntheticMusicRelease() });
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var snapshotService = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);
        var filterStage = new FilterStage(new FilterProfileLoader(context), new SettingsReader(context), context, time);
        var releaseLookup = new InMemoryReleaseLookup();

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");
        var result = await SearchEndpoint.HandleTorznabAsync(
            "music",
            "Synthetic Artist",
            Array.Empty<int>(),
            50,
            0,
            "secret-api-key",
            snapshotService,
            filterStage,
            releaseLookup,
            new RecentSearchLog(),
            httpContext.Request,
            CancellationToken.None);

        using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteAsync(httpContext);
        body.Seek(0, SeekOrigin.Begin);
        var rendered = new StreamReader(body).ReadToEnd();

        Assert.Contains("<title>Synthetic Artist - Synthetic Album (2026) [FLAC]</title>", rendered);
        // The torznab wrapper always renders application/x-bittorrent regardless of the
        // underlying candidate's own protocol (see IndexerXmlWriter: enclosure MIME is
        // driven by the requesting protocol family, not the release's ProtocolKind).
        Assert.Contains("type=\"application/x-bittorrent\"", rendered);
        Assert.Contains("<torznab:attr name=\"category\" value=\"3010\" />", rendered);
    }

    [Fact]
    public async Task T_music_renders_identically_through_the_newznab_wrapper()
    {
        using var context = CreateContext();
        var source = new FakeUpstreamSource("synthsrc", searchResults: new[] { SyntheticMusicRelease() });
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var snapshotService = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);
        var filterStage = new FilterStage(new FilterProfileLoader(context), new SettingsReader(context), context, time);
        var releaseLookup = new InMemoryReleaseLookup();

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");
        var result = await SearchEndpoint.HandleNewznabAsync(
            "music",
            "Synthetic Artist",
            Array.Empty<int>(),
            50,
            0,
            "secret-api-key",
            snapshotService,
            filterStage,
            releaseLookup,
            new RecentSearchLog(),
            httpContext.Request,
            CancellationToken.None);

        using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteAsync(httpContext);
        body.Seek(0, SeekOrigin.Begin);
        var rendered = new StreamReader(body).ReadToEnd();

        Assert.Contains("xmlns:newznab=\"http://torznab.com/schemas/2015/feed\"", rendered);
        Assert.Contains("<newznab:attr name=\"category\" value=\"3010\" />", rendered);
    }
}
