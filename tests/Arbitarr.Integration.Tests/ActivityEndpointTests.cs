using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #55 step 3: covers <c>GET /api/activity</c> against the real Host — that it is reachable without
/// an admin key, that its filters reach the store, and that its paging contract holds end to end.
///
/// The un-gated assertion is the load-bearing one here. Classification is by
/// <see cref="Arbitarr.Api.Routing.RouteClassification"/> and path prefix, never by HTTP verb, and
/// this route is <c>PublicRead</c> per plan §3.2 — confirmed rather than provisional now that #59
/// has closed with D2 unamended (the admin key gates mutating actions; reading is not one). If a
/// later change moves this route under <c>/api/admin/</c> or gates it, this test fails loudly, which
/// is the intent: the Activity surface's frontend client sends no key for it.
/// </summary>
public sealed class ActivityEndpointTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string Route = "/api/activity";

    private readonly ArbitarrWebApplicationFactory _factory;

    public ActivityEndpointTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Writes events through the real repository in the Host's own DI container, so these tests
    /// exercise the same store the endpoint reads rather than a parallel fixture.
    /// </summary>
    private async Task SeedAsync(params (EventKind Kind, string Summary)[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<EventRepository>();

        foreach (var (kind, summary) in events)
        {
            await repository.AddAsync(kind, summary, reason: "seeded by a test", null, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Route_requires_no_admin_api_key()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        // 503 is what an admin-gated route answers on a server with no key configured (see
        // AdminApiKeyFilter); an un-gated read must never reach that branch.
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Route_rejects_mutating_verbs_with_405()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Serves_events_most_recent_first()
    {
        await SeedAsync(
            (EventKind.WorkerCycle, "activity-order first"),
            (EventKind.WorkerCycle, "activity-order second"));

        using var client = _factory.CreateClient();
        var page = await client.GetFromJsonAsync<ActivityPageResponse>(Route);

        Assert.NotNull(page);
        var ours = page!.Events.Where(e => e.Summary.StartsWith("activity-order", StringComparison.Ordinal)).ToList();
        Assert.Equal("activity-order second", ours[0].Summary);
        Assert.Equal("activity-order first", ours[1].Summary);
    }

    [Fact]
    public async Task Filters_by_kind()
    {
        await SeedAsync(
            (EventKind.SourceFailed, "activity-kind failure"),
            (EventKind.SearchServed, "activity-kind search"));

        using var client = _factory.CreateClient();
        var page = await client.GetFromJsonAsync<ActivityPageResponse>($"{Route}?kind=sourceFailed");

        Assert.NotNull(page);
        Assert.All(page!.Events, e => Assert.Equal("sourceFailed", e.Kind));
        Assert.Contains(page.Events, e => e.Summary == "activity-kind failure");
    }

    /// <summary>
    /// An unknown kind is a 400, not a silently unfiltered list: answering a question the caller did
    /// not ask is worse than refusing the one they did.
    /// </summary>
    [Theory]
    [InlineData("notAKind")]
    [InlineData("99")]
    public async Task Rejects_an_unknown_kind_with_400(string kind)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"{Route}?kind={kind}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pages_with_a_cursor_without_repeating_a_row()
    {
        await SeedAsync(Enumerable.Range(0, 6)
            .Select(i => (EventKind.WorkerCycle, $"activity-page {i}"))
            .ToArray());

        using var client = _factory.CreateClient();

        var first = await client.GetFromJsonAsync<ActivityPageResponse>($"{Route}?limit=3");
        Assert.NotNull(first);
        Assert.Equal(3, first!.Events.Count);
        Assert.NotNull(first.NextCursor);

        var second = await client.GetFromJsonAsync<ActivityPageResponse>($"{Route}?limit=3&cursor={first.NextCursor}");
        Assert.NotNull(second);

        var firstSummaries = first.Events.Select(e => e.Summary).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(second!.Events, e => firstSummaries.Contains(e.Summary));
    }

    /// <summary>
    /// AC9: a timestamp must be unambiguous about its timezone. The wire format is ISO-8601 carrying
    /// an explicit offset, so a reader (and the surface) can resolve the actual instant rather than
    /// guessing whose local clock it belongs to.
    /// </summary>
    [Fact]
    public async Task Serves_timestamps_with_an_explicit_offset()
    {
        await SeedAsync((EventKind.WorkerCycle, "activity-timestamp"));

        using var client = _factory.CreateClient();
        var json = await client.GetStringAsync(Route);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var occurredAt = document.RootElement
            .GetProperty("events")
            .EnumerateArray()
            .First()
            .GetProperty("occurredAt")
            .GetString();

        Assert.NotNull(occurredAt);
        // Either a numeric offset (+00:00) or the Z designator is unambiguous; a bare local-looking
        // timestamp with neither is exactly what AC9 forbids.
        Assert.True(
            occurredAt!.EndsWith("Z", StringComparison.Ordinal)
                || occurredAt.Contains('+', StringComparison.Ordinal)
                || occurredAt.LastIndexOf('-') > occurredAt.IndexOf('T'),
            $"Timestamp '{occurredAt}' carries no timezone offset (AC9).");
    }
}
