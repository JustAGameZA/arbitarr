using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #55 step 2 (plan §5: "each event kind is emitted at its trigger point, asserted with a fixture
/// exercising that path"). This drives a real Torznab search through the full Host pipeline and
/// asserts the resulting event is visible on <c>GET /api/activity</c> — the emission is wired into
/// the live path, not merely reachable via DI in a unit test. It is modelled directly on
/// <see cref="SearchRecentLogTests"/>, which exists because that exact gap once shipped.
///
/// It also carries that test's apikey-leak assertion, and for a stronger reason here: the recent
/// searches log is in-memory and forgets on restart, whereas an apikey written into an event row
/// would be PERSISTED to the SQLite file in the config bind mount and then served un-gated over
/// <c>/api/activity</c> for the whole retention window. Plan §9 forbids it; this proves it.
/// </summary>
public sealed class ActivityEmissionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ApiKey = "secret-api-key";

    private readonly WebApplicationFactory<Program> _factory;

    public ActivityEmissionTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-activity-emission-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", configDirectory);
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                // One fake upstream returning a single result, so the search path runs end to end
                // without any real upstream being reachable. 192.0.2.x is RFC 5737 TEST-NET-1.
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "activity-emission-fake-source",
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Activity Emission Probe Release",
                            Guid = "activity-emission-probe-1",
                            PubDate = DateTimeOffset.UtcNow,
                            Size = 654_321,
                            Link = new Uri("http://192.0.2.71:8080/getnzb/activity-emission-probe-1"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Usenet,
                        },
                    }));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
            });
        });
    }

    [Fact]
    public async Task A_real_search_records_a_searchServed_event_that_says_how_it_was_served()
    {
        const string queryText = "the.activity.emission.probe.s01";

        using var client = _factory.CreateClient();

        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q={Uri.EscapeDataString(queryText)}&apikey={Uri.EscapeDataString(ApiKey)}");
        searchResponse.EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<ActivityPageResponse>("/api/activity?kind=searchServed");

        Assert.NotNull(page);
        var served = Assert.Single(page!.Events, e => e.Reason is not null && e.Reason.Contains(queryText, StringComparison.Ordinal));

        // AC2: the row states what happened AND why, rather than only naming an event.
        Assert.Equal("searchServed", served.Kind);
        Assert.False(string.IsNullOrWhiteSpace(served.Summary));

        // AC3: cache-served and live-query-served are distinguishable from the row itself. This
        // first search cannot have been served from cache -- nothing had populated it yet -- so the
        // summary must say so rather than being a generic "search served".
        Assert.Contains("live query", served.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other half of AC3, and the half that catches the mistake worth catching: repeating the
    /// same query is served from the cache the first one populated, and must SAY so.
    ///
    /// Asserting only the live case would pass against an implementation that labels every search
    /// "live" — and asserting only the cache case would pass against one that labels every search
    /// "from cache", which is precisely the defect that shipped here first (CacheBand.Fresh covers a
    /// fresh cache hit AND a just-completed fetch, so branching on the band alone conflates them).
    /// Two searches, two different labels, is the assertion that pins the distinction.
    /// </summary>
    [Fact]
    public async Task Repeating_a_search_records_it_as_cache_served_not_live()
    {
        const string queryText = "the.cache.served.probe.s01";
        var url = $"/torznab/api?t=search&q={Uri.EscapeDataString(queryText)}&apikey={Uri.EscapeDataString(ApiKey)}";

        using var client = _factory.CreateClient();

        // First search populates the cache; second is served from it.
        (await client.GetAsync(url)).EnsureSuccessStatusCode();
        (await client.GetAsync(url)).EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<ActivityPageResponse>("/api/activity?kind=searchServed");
        Assert.NotNull(page);

        // Newest first, so [0] is the second (cache-served) search and [1] the first (live) one.
        var ours = page!.Events
            .Where(e => e.Reason is not null && e.Reason.Contains(queryText, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, ours.Count);
        Assert.Contains("cache", ours[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live query", ours[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live query", ours[1].Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plan §9 / AC-adjacent: an event row must never carry a credential. The client's apikey
    /// travels on the search request's raw query string, so the emission path is exactly where one
    /// could leak into a persisted, un-gated surface.
    /// </summary>
    [Fact]
    public async Task A_real_search_never_writes_the_client_apikey_into_the_activity_feed()
    {
        using var client = _factory.CreateClient();

        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q=apikey-leak-probe&apikey={Uri.EscapeDataString(ApiKey)}");
        searchResponse.EnsureSuccessStatusCode();

        var body = await client.GetStringAsync("/api/activity");

        Assert.Contains("apikey-leak-probe", body);
        Assert.DoesNotContain(ApiKey, body);
    }

    /// <summary>
    /// AC4, and the visible difference from today: events outlive the process, unlike the
    /// pipeline counters (System.tsx:123-124) and the in-memory recent-searches ring buffer.
    ///
    /// A container restart cannot be staged in-process, so this asserts the property that makes
    /// surviving one possible — the row is in the database, readable through a brand-new DI scope
    /// that shares no in-memory state with the request that produced it.
    /// </summary>
    [Fact]
    public async Task A_recorded_event_is_readable_from_a_fresh_scope_rather_than_process_memory()
    {
        using var client = _factory.CreateClient();

        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q=durability-probe&apikey={Uri.EscapeDataString(ApiKey)}");
        searchResponse.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.Events.EventRepository>();

        var stored = await repository.QueryAsync(
            new Arbitarr.Data.Events.EventQuery(Kind: Arbitarr.Data.Entities.EventKind.SearchServed),
            CancellationToken.None);

        Assert.Contains(stored.Events, e => e.Reason is not null && e.Reason.Contains("durability-probe", StringComparison.Ordinal));
    }
}
