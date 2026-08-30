using Arbitarr.Api.Search;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// SEC-L1 amendment: <see cref="SearchEndpoint"/> embeds the calling client's own resolved apikey
/// into every rendered <c>/download/{proxyGuid}?apikey=...</c> link, so a link copied/leaked from
/// one client's response cannot be replayed by a party who never had that client's key. This test
/// pins that two different callers searching for the same release get back two distinct rendered
/// links, each carrying its own apikey.
/// </summary>
public sealed class DownloadLinkPerClientTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-downloadlink-test-{Guid.NewGuid():N}.db");

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

    private async Task<string> RenderWithCallerKeyAsync(string callerApiKey)
    {
        var source = new FakeUpstreamSource("eztv", searchResults: new[] { TestReleases.Torrent().Candidate });
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var snapshotService = new PaginationSnapshotService(mergeStage, store, time);
        var releaseLookup = new InMemoryReleaseLookup();

        using var context = CreateContext();
        var filterStage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            time);

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost");

        var result = await SearchEndpoint.HandleTorznabAsync(
            "search",
            null,
            Array.Empty<int>(),
            50,
            0,
            callerApiKey,
            snapshotService,
            filterStage,
            releaseLookup,
            httpContext.Request,
            CancellationToken.None);

        using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteAsync(httpContext);
        body.Seek(0, SeekOrigin.Begin);
        return new StreamReader(body).ReadToEnd();
    }

    [Fact]
    public async Task Two_different_callers_get_two_distinct_rendered_download_links()
    {
        var renderedForClientA = await RenderWithCallerKeyAsync("secret-api-key");
        var renderedForClientB = await RenderWithCallerKeyAsync("secret-api-key-b");

        Assert.Contains("apikey=secret-api-key\"", renderedForClientA);
        Assert.DoesNotContain("apikey=secret-api-key-b", renderedForClientA);

        Assert.Contains("apikey=secret-api-key-b", renderedForClientB);
        Assert.DoesNotContain("apikey=secret-api-key\"", renderedForClientB);

        Assert.NotEqual(renderedForClientA, renderedForClientB);
    }
}
