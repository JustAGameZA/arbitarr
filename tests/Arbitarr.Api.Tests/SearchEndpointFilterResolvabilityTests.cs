using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// End-to-end assertion that an enforced (shadow OFF) suppression is a deny, full stop: the
/// withheld release must be absent from search results AND must not resolve via
/// <see cref="InMemoryReleaseLookup"/> (i.e. would not be downloadable through
/// <c>/download/{proxyGuid}</c>). Shadow mode ON keeps the release both present (annotated) and
/// resolvable, since shadow mode never withholds. Exercises the real
/// <see cref="SearchEndpoint.HandleTorznabAsync"/> entry point (not just <see cref="FilterStage"/>
/// in isolation) so the <c>RecordRange</c> post-filter registration is verified as actually wired.
/// </summary>
public sealed class SearchEndpointFilterResolvabilityTests : IDisposable
{
    private readonly string _dbPath;
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public SearchEndpointFilterResolvabilityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-searchendpoint-resolve-test-{Guid.NewGuid():N}.db");
    }

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

    private static async Task SeedDenyRuleProfileAsync(ArbitarrDbContext context)
    {
        var profile = new FilterProfileEntry
        {
            Name = "Default",
            IsDefault = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        context.FilterProfiles.Add(profile);
        await context.SaveChangesAsync();

        context.FilterRules.Add(new FilterRuleEntry
        {
            FilterProfileId = profile.Id,
            Name = "deny-cam",
            IsAllow = false,
            Pattern = "CAM",
            Precedence = 2,
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await context.SaveChangesAsync();
    }

    private static async Task SetShadowModeAsync(ArbitarrDbContext context, bool shadowMode)
    {
        context.Settings.Add(new SettingEntry
        {
            Name = nameof(Core.Settings.SettingKey.ShadowMode),
            Value = shadowMode.ToString(),
            UpdatedAt = Now,
        });
        await context.SaveChangesAsync();
    }

    private sealed class SingleReleaseUpstreamSource : IUpstreamSource
    {
        private readonly ReleaseCandidate _candidate;

        public SingleReleaseUpstreamSource(ReleaseCandidate candidate) => _candidate = candidate;

        public string Name => "TestSource";

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReleaseCandidate>>(new[] { _candidate });

        public Task<SourceCaps> GetCapsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceCaps(Array.Empty<int>(), false, false, null));

        public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    private static ReleaseCandidate DenyMatchedCandidate() => new()
    {
        Title = "Some.Movie.2026.CAM.x264",
        Guid = "guid-1",
        PubDate = Now,
        Size = 123,
        Link = new Uri("https://example.invalid/1"),
    };

    private static async Task<(IReadOnlyList<RenderedRelease> Releases, string ProxyGuid, InMemoryReleaseLookup Lookup)> RunSearchAsync(
        ArbitarrDbContext context,
        ReleaseCandidate candidate)
    {
        var source = new SingleReleaseUpstreamSource(candidate);
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var snapshotStore = new QuerySnapshotStore(context);
        var snapshotService = new PaginationSnapshotService(mergeStage, snapshotStore, new FakeTimeProvider(Now));
        var filterStage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now));
        var lookup = new InMemoryReleaseLookup();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("indexer.example.invalid");

        var expectedProxyGuid = new RenderedRelease(source.Name, candidate).ProxyGuid;

        await SearchEndpoint.HandleTorznabAsync(
            "movie",
            "movie",
            Array.Empty<int>(),
            100,
            0,
            "caller-api-key",
            snapshotService,
            filterStage,
            lookup,
            httpContext.Request,
            CancellationToken.None);

        // Re-run FilterStage's own view of the merged set to hand callers the annotated result too.
        var merged = await mergeStage.MergeAsync(new SearchQuery("movie", Array.Empty<int>(), 100, 0));
        var filtered = await filterStage.ApplyAsync(merged.Releases, "movie");

        return (filtered, expectedProxyGuid, lookup);
    }

    [Fact]
    public async Task ShadowModeOff_DenyMatch_AbsentFromResultsAndNotResolvable()
    {
        using var context = CreateContext();
        await SeedDenyRuleProfileAsync(context);
        await SetShadowModeAsync(context, shadowMode: false);

        var (releases, proxyGuid, lookup) = await RunSearchAsync(context, DenyMatchedCandidate());

        Assert.Empty(releases);
        var resolved = await lookup.FindAsync(proxyGuid);
        Assert.Null(resolved);
    }

    [Fact]
    public async Task ShadowModeOn_DenyMatch_PresentAndResolvable()
    {
        using var context = CreateContext();
        await SeedDenyRuleProfileAsync(context);
        await SetShadowModeAsync(context, shadowMode: true);

        var (releases, proxyGuid, lookup) = await RunSearchAsync(context, DenyMatchedCandidate());

        var release = Assert.Single(releases);
        Assert.NotNull(release.SuppressionAnnotation);
        var resolved = await lookup.FindAsync(proxyGuid);
        Assert.NotNull(resolved);
        Assert.Equal(proxyGuid, resolved!.ProxyGuid);
    }
}
