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

    /// <summary>
    /// arb-2b6 (audit F-010): the bead's actual complaint. Two id-based searches carry no query
    /// text, so the old reason rendered both as <c>Query '' (tvsearch)</c> and an operator could not
    /// tell a zero-result cache hit from a live query with real results fired in the same second.
    ///
    /// <para>Asserted PER ROW (CLAUDE.md §4): each search's own event is located by its own tvdbid
    /// and checked individually. "Some row mentions 74796" would still pass if one descriptor were
    /// written to every row.</para>
    /// </summary>
    [Fact]
    public async Task Two_searches_differing_only_by_id_produce_different_reasons_and_details()
    {
        using var client = _factory.CreateClient();

        // No q= on either: this is the id-based shape the finding is about.
        var first = await client.GetAsync(
            $"/torznab/api?t=tvsearch&tvdbid=74796&apikey={Uri.EscapeDataString(ApiKey)}");
        first.EnsureSuccessStatusCode();

        var second = await client.GetAsync(
            $"/torznab/api?t=tvsearch&tvdbid=81797&apikey={Uri.EscapeDataString(ApiKey)}");
        second.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.Events.EventRepository>();

        var stored = await repository.QueryAsync(
            new Arbitarr.Data.Events.EventQuery(Kind: Arbitarr.Data.Entities.EventKind.SearchServed),
            CancellationToken.None);

        var firstRow = Assert.Single(
            stored.Events,
            e => e.Reason is not null && e.Reason.Contains("tvdbid=74796", StringComparison.Ordinal));
        var secondRow = Assert.Single(
            stored.Events,
            e => e.Reason is not null && e.Reason.Contains("tvdbid=81797", StringComparison.Ordinal));

        // Per row: each carries its OWN id and not the other's.
        Assert.DoesNotContain("tvdbid=81797", firstRow.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("tvdbid=74796", secondRow.Reason!, StringComparison.Ordinal);

        Assert.NotNull(firstRow.Detail);
        Assert.NotNull(secondRow.Detail);
        Assert.Contains("tvdbid=74796", firstRow.Detail!, StringComparison.Ordinal);
        Assert.Contains("tvdbid=81797", secondRow.Detail!, StringComparison.Ordinal);
        Assert.NotEqual(firstRow.Detail, secondRow.Detail);

        // The regression itself: neither reason is the old text-only rendering.
        Assert.DoesNotContain("Query ''", firstRow.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain("Query ''", secondRow.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-p1y: identical searches must FOLD onto one row rather than writing one row each.
    ///
    /// <para><b>WHY THIS NEEDS THREE SEARCHES, NOT TWO.</b> The obvious spelling — fire the same
    /// query twice and expect one row — cannot work and would fail against a correct
    /// implementation. The first search is served live and the second from the cache it populated,
    /// so the two carry DIFFERENT summaries ("from a live query" vs "from cache"), and Summary is
    /// part of the coalescing identity. They are genuinely two different events and
    /// <see cref="Repeating_a_search_records_it_as_cache_served_not_live"/> asserts exactly that.
    /// Searches two and three are the first pair that is identical in all six identity fields, so
    /// the fold this bead is about is the one between THEM.</para>
    ///
    /// <para><b>THE DEFECT IT PINS.</b> While the reason ended with the elapsed <c>{ms}ms</c>, the
    /// Reason differed on every occurrence, so no two searches ever folded. Asserting RepeatCount
    /// rather than merely counting rows is deliberate: a store that silently DE-DUPLICATED would
    /// also produce one row here, and would be a different bug (it throws away the fact that the
    /// search happened twice — exactly what EventEntry.RepeatCount's doc comment says the 71-row
    /// flood must not become). One row AND a count of 2 is the assertion that distinguishes
    /// coalescing from dropping.</para>
    /// </summary>
    [Fact]
    public async Task Two_identical_searches_fold_onto_one_row_with_a_repeat_count()
    {
        const string queryText = "the.coalescing.probe.s01";
        var url = $"/torznab/api?t=search&q={Uri.EscapeDataString(queryText)}&apikey={Uri.EscapeDataString(ApiKey)}";

        using var client = _factory.CreateClient();

        // First is served live and is its own row; the second and third are both cache-served and
        // are the identical pair that must fold.
        (await client.GetAsync(url)).EnsureSuccessStatusCode();
        (await client.GetAsync(url)).EnsureSuccessStatusCode();
        (await client.GetAsync(url)).EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.Events.EventRepository>();

        var stored = await repository.QueryAsync(
            new Arbitarr.Data.Events.EventQuery(Kind: Arbitarr.Data.Entities.EventKind.SearchServed),
            CancellationToken.None);

        // Scoped to this test's own query text: the fixture is shared, so other tests' rows are in
        // the same store.
        var ours = stored.Events
            .Where(e => e.Reason is not null && e.Reason.Contains(queryText, StringComparison.Ordinal))
            .ToList();

        // Three searches, two rows: the live one, and the two cache-served ones folded into one.
        Assert.Equal(2, ours.Count);

        var cacheServed = Assert.Single(
            ours,
            e => e.Summary.Contains("from cache", StringComparison.OrdinalIgnoreCase));
        var liveServed = Assert.Single(
            ours,
            e => e.Summary.Contains("live query", StringComparison.OrdinalIgnoreCase));

        // The fold itself, asserted per row (CLAUDE.md §4). RepeatCount counts the first
        // occurrence, so two occurrences on one row is 2, not 1.
        Assert.Equal(2, cacheServed.RepeatCount);
        Assert.NotNull(cacheServed.LastRepeatedAt);

        // The live row is untouched by the fold: it repeated zero times, so its count stays 1 and
        // LastRepeatedAt stays null rather than being stamped alongside the other row's.
        Assert.Equal(1, liveServed.RepeatCount);
        Assert.Null(liveServed.LastRepeatedAt);

        // The mechanism, stated directly: what makes the pair identical is that the Reason is
        // exactly the descriptor and carries no per-occurrence value. Asserted as full-string
        // equality rather than DoesNotContain("ms") — an absence assertion over a two-letter
        // substring would pass for the wrong reason the moment a query text happened to contain
        // one, and equality is what actually pins "nothing per-occurrence was appended".
        Assert.Equal($"Query '{queryText}' (search)", cacheServed.Reason);
    }
}
