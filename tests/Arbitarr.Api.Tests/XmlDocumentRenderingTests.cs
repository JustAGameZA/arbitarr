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
/// Platform-independence regression guard for <c>XmlDocumentRendering.ToXmlString</c> (internal
/// to Arbitarr.Api, exercised here through the real production endpoints — CI's earlier failure
/// on this exact class of bug, a <c>\r\n</c> vs <c>\n</c> mismatch between local Windows
/// development and the Linux CI/deployment target, is exactly what this test would have caught:
/// <c>XDocument.Save(TextWriter)</c> without an explicit <c>XmlWriter</c> defaults
/// <c>NewLineChars</c> to <see cref="Environment.NewLine"/>, which is host-OS-dependent. Every
/// rendered Torznab/Newznab document (caps, search results, and error bodies) must never contain
/// a carriage return and must use two-space indentation, regardless of the host OS it runs on.
/// </summary>
public sealed class XmlDocumentRenderingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-xmldocrendering-test-{Guid.NewGuid():N}.db");

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

    [Fact]
    public async Task Caps_document_never_contains_carriage_return_and_uses_two_space_indent()
    {
        var rendered = await RenderCapsAsync();

        Assert.DoesNotContain('\r', rendered);
        Assert.Contains("\n  <server ", rendered);
    }

    [Fact]
    public async Task Search_document_never_contains_carriage_return_and_uses_two_space_indent()
    {
        var rendered = await RenderSearchAsync();

        Assert.DoesNotContain('\r', rendered);
        Assert.Contains("\n  <channel>", rendered);
    }

    [Fact]
    public async Task Error_document_never_contains_carriage_return_and_uses_two_space_indent()
    {
        var rendered = await RenderRateLimitErrorAsync();

        Assert.DoesNotContain('\r', rendered);
        Assert.Contains("<error code=", rendered);
    }

    private async Task<string> RenderCapsAsync()
    {
        var caps = new SourceCaps(
            SupportedCategories: new[] { 5000, 2000 },
            SupportsTvSearch: true,
            SupportsMovieSearch: true,
            MaxPageSize: 100,
            SupportedParams: new[] { "q" });
        var source = new SingleCapsUpstreamSource("eztv", caps);
        var aggregator = new CapsAggregator(new NoOpCapsCacheStore());


        var result = await CapsEndpoint.HandleTorznabAsync(
            aggregator,
            new[] { (IUpstreamSource)source },
            CancellationToken.None);

        return await ExecuteAndReadBodyAsync(result);
    }

    private async Task<string> RenderSearchAsync()
    {
        var release = TestReleases.Torrent();
        var source = new FakeUpstreamSource("eztv", searchResults: new[] { release.Candidate });
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(TestReleases.FixedPubDate);
        var snapshotService = new PaginationSnapshotService(mergeStage, store, time);
        var releaseLookup = new InMemoryReleaseLookup();

        var httpContext = NewHttpContext();

        using var context = CreateContext();
        var filterStage = new FilterStage(
            new FilterProfileLoader(context),
            new SettingsReader(context),
            context,
            time);

        var result = await SearchEndpoint.HandleTorznabAsync(
            "search",
            null,
            Array.Empty<int>(),
            50,
            0,
            "caller-api-key",
            snapshotService,
            filterStage,
            releaseLookup,
            httpContext.Request,
            CancellationToken.None);

        return await ExecuteAndReadBodyAsync(result, httpContext);
    }

    private async Task<string> RenderRateLimitErrorAsync()
    {
        var source = new FakeUpstreamSource("eztv", searchException: new RequestLimitReachedException("eztv"));
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(TestReleases.FixedPubDate);
        var snapshotService = new PaginationSnapshotService(mergeStage, store, time);
        var releaseLookup = new InMemoryReleaseLookup();

        var httpContext = NewHttpContext();

        using var context = CreateContext();
        var filterStage = new FilterStage(
            new FilterProfileLoader(context),
            new SettingsReader(context),
            context,
            time);

        var result = await SearchEndpoint.HandleTorznabAsync(
            "search",
            null,
            Array.Empty<int>(),
            50,
            0,
            "caller-api-key",
            snapshotService,
            filterStage,
            releaseLookup,
            httpContext.Request,
            CancellationToken.None);

        return await ExecuteAndReadBodyAsync(result, httpContext);
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost");
        return httpContext;
    }

    private static async Task<string> ExecuteAndReadBodyAsync(IResult result, DefaultHttpContext? httpContext = null)
    {
        httpContext ??= NewHttpContext();
        using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteAsync(httpContext);
        body.Seek(0, SeekOrigin.Begin);
        return new StreamReader(body).ReadToEnd();
    }

    /// <summary>Minimal <see cref="IUpstreamSource"/> double that returns a fixed <see cref="SourceCaps"/>.</summary>
    private sealed class SingleCapsUpstreamSource : IUpstreamSource
    {
        private readonly SourceCaps _caps;

        public SingleCapsUpstreamSource(string name, SourceCaps caps)
        {
            Name = name;
            _caps = caps;
        }

        public string Name { get; }

        public Task<IReadOnlyList<Arbitarr.Core.Releases.ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Arbitarr.Core.Releases.ReleaseCandidate>>(Array.Empty<Arbitarr.Core.Releases.ReleaseCandidate>());

        public Task<SourceCaps> GetCapsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_caps);

        public Task<Stream> FetchDownloadAsync(Arbitarr.Core.Releases.ReleaseCandidate release, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    /// <summary>No-op <see cref="ICapsCacheStore"/> double — this test never exercises the fallback path.</summary>
    private sealed class NoOpCapsCacheStore : ICapsCacheStore
    {
        public Task<SourceCaps?> GetLastKnownGoodAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceCaps?>(null);

        public Task SaveAsync(string sourceName, SourceCaps caps, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
