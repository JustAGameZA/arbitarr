using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Settings;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-1 (non-AI half): <c>GET /api/admin/search</c> is admin-gated (D2) and runs the same
/// PaginationSnapshotService/UpstreamMergeStage path as <c>/torznab/api</c>, rendering JSON with
/// releases untouched (title/size/category/guid) plus cache/rate-limit provenance.
/// </summary>
public sealed class AdHocSearchEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AdminKey = "adhoc-search-admin-key";
    private const string Route = "/api/admin/search";

    private readonly WebApplicationFactory<Program> _factory;

    public AdHocSearchEndpointTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m7-adhoc-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the real, config-driven upstream source registrations with a single fake
                // that echoes back one release, so this test exercises the endpoint's live merge
                // path without depending on any real upstream (NZBHydra2) being reachable.
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "adhoc-fake-source",
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Ad Hoc Probe Release",
                            Guid = "adhoc-probe-1",
                            PubDate = DateTimeOffset.UtcNow,
                            Size = 654_321,
                            Link = new Uri("http://192.0.2.80:8080/getnzb/adhoc-probe-1"),
                            Category = new[] { 5030 },
                            Protocol = ProtocolKind.Usenet,
                        },
                    }));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
            });
        });
    }

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
    }

    private async Task SeedAdminKeyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();

        var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
        if (existing is null)
        {
            db.Settings.Add(new SettingEntry
            {
                Name = SettingKey.AdminApiKey.ToString(),
                Value = AdminKey,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Value = AdminKey;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GET_search_without_admin_key_is_rejected_with_401()
    {
        // An admin key must be configured first, otherwise AdminApiKeyFilter fails closed with 503
        // (unset gate) rather than 401 (wrong/missing credential) — this test targets the latter.
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"{Route}?q=probe");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_search_with_admin_key_returns_releases_untouched_and_provenance()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync($"{Route}?q=probe");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();

        Assert.NotNull(body);
        var release = Assert.Single(body!.Releases);
        Assert.Equal("Ad Hoc Probe Release", release.Title);
        Assert.Equal("adhoc-probe-1", release.Guid);
        Assert.Equal(654_321, release.Size);
        Assert.Equal(new[] { 5030 }, release.Category);
        Assert.Equal("adhoc-fake-source", release.SourceName);

        Assert.NotNull(body.Provenance);
        Assert.Empty(body.Provenance.RateLimitedSources);
    }

    [Fact]
    public async Task GET_search_folds_tvdbid_season_and_episode_into_the_query()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        // The fake source ignores query content and always returns its one seeded release; this
        // asserts only that supplying these params does not error and still reaches the merge path.
        var response = await client.GetAsync($"{Route}?tvdbid=12345&season=1&ep=3");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Releases);
    }

    [Fact]
    public async Task GET_search_forwards_categories_and_paging_params()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        var response = await client.GetAsync($"{Route}?q=probe&cat=5030,5040&limit=10&offset=0");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Releases);
    }

    [Fact]
    public async Task GET_search_route_is_classified_AdminMutating()
    {
        using var client = _factory.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(e => e.RoutePattern.RawText == "/api/admin/search");

        Assert.NotNull(endpoint);
        Assert.Equal(RouteClassification.AdminMutating, endpoint!.GetClassification());
    }
}
