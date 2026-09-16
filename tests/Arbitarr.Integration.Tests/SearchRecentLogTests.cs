using System.Net.Http.Json;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Integration.Tests.TestSupport;
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
public sealed class SearchRecentLogTests : IAsyncLifetime
{
    private const string ApiKey = "secret-api-key";

    private readonly ArbitarrWebApplicationFactory _root;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configDirectory;

    public SearchRecentLogTests()
    {
        _configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m2-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);

        _root = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
        _factory = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                // Replace the real, config-driven upstream source registrations with a single fake
                // that returns one result, so this test exercises SearchEndpoint's live recording
                // path without depending on any real upstream (NZBHydra2) being reachable.
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<ISourceRegistry>();
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
                services.AddSingleton<ISourceRegistry>(sp => new StaticSourceRegistry(sp.GetServices<IUpstreamSource>().ToArray()));
            });
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// This class OWNS its host so disposal drains it before the delete — see
    /// <see cref="CategoryParamCapTests.DisposeAsync"/> for the full account of why the shared
    /// <c>IClassFixture</c> this class used to inject made the delete throw (arb-gphi fix-up).
    /// </summary>
    public async Task DisposeAsync()
    {
        await _root.DisposeAsync();
        ConfigDirectoryTeardown.Delete(_configDirectory);
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
