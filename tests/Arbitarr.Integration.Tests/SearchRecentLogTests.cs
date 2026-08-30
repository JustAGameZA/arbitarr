using System.Net.Http.Json;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M2-5 gap: <see cref="RecentSearchLog"/> must be written by the live Torznab/Newznab search
/// path (SearchEndpoint.ExecuteAsync), not only reachable via direct DI in tests, so
/// <c>/api/searches/recent</c> is populated in real deployments. This drives a real Torznab
/// search through the full Host pipeline with a configured client apikey and proves: (1) the
/// query text lands in the recent-searches log, and (2) the client's apikey — which travels on
/// the request's raw query string — never appears anywhere in that response body.
/// </summary>
public sealed class SearchRecentLogTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ApiKey = "secret-api-key";

    private readonly WebApplicationFactory<Program> _factory;

    public SearchRecentLogTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m2-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                // Replace the real, config-driven upstream source registrations with a single fake
                // that returns one result, so this test exercises SearchEndpoint's live recording
                // path without depending on any real upstream (NZBHydra2) being reachable.
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "recent-log-fake-source",
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Recent Log Probe Release",
                            Guid = "recent-log-probe-1",
                            PubDate = DateTimeOffset.UtcNow,
                            Size = 123_456,
                            Link = new Uri("http://192.0.2.70:8080/getnzb/recent-log-probe-1"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Usenet,
                        },
                    }));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
            });
        });
    }

    [Fact]
    public async Task Real_torznab_search_records_query_in_recent_searches_and_never_leaks_apikey()
    {
        const string queryText = "the.recent.log.probe.s01";

        using var client = _factory.CreateClient();

        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q={Uri.EscapeDataString(queryText)}&apikey={Uri.EscapeDataString(ApiKey)}");
        searchResponse.EnsureSuccessStatusCode();

        var recentBody = await client.GetStringAsync("/api/searches/recent");

        Assert.Contains(queryText, recentBody);
        Assert.DoesNotContain(ApiKey, recentBody);

        var recentEntries = await client.GetFromJsonAsync<List<RecentSearchEntry>>("/api/searches/recent");
        Assert.NotNull(recentEntries);
        Assert.Contains(recentEntries!, e => e.Query == queryText);
    }
}
