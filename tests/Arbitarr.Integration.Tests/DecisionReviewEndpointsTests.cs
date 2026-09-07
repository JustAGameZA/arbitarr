using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Dashboard;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #54 steps 3-5 against the real Host: the decisions read, the review write, and the agreement
/// aggregate, over the shared event store's <see cref="EventKind.Decision"/> rows.
///
/// THE GATE ASSERTIONS HERE ARE NOT REDUNDANT WITH THE ENUMERATION SWEEP, AND MUST NOT BE DELETED
/// AS SUCH. <see cref="AdminApiKeyRouteEnumerationTests"/> deliberately skips <c>{id}</c>-templated
/// routes — it cannot invent an id that resolves — so the review route
/// (<c>/api/admin/decisions/{id}/review</c>) is invisible to that sweep. The sweep going green is
/// therefore NOT evidence that this route is gated; only
/// <see cref="Review_route_is_admin_gated"/> below is. A templated admin route that no test names
/// by hand is a route nothing checks.
/// </summary>
public sealed class DecisionReviewEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string DecisionsRoute = "/api/decisions";
    private const string AgreementRoute = "/api/decisions/agreement";

    private readonly ArbitarrWebApplicationFactory _factory;

    public DecisionReviewEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Writes decisions through the real repository in the Host's own DI container, so these tests
    /// exercise the same store the endpoints read rather than a parallel fixture.
    /// </summary>
    private async Task<List<long>> SeedDecisionsAsync(params bool[] shadowModes)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<EventRepository>();

        var ids = new List<long>();
        foreach (var shadowMode in shadowModes)
        {
            var entry = await repository.AddAsync(
                EventKind.Decision,
                shadowMode ? "Release flagged in shadow mode (still served)" : "Release suppressed",
                reason: "seeded by a test",
                sourceDisplayName: null,
                detail: "layer=test-rule; release=test-release",
                CancellationToken.None,
                shadowMode: shadowMode);

            ids.Add(entry.Id);
        }

        return ids;
    }

    private async Task SeedAdminKeyAsync()
    {
        // Upsert rather than Add: the factory's SQLite database is shared across every [Fact] in
        // this IClassFixture-scoped class, so a second test seeding the same key would otherwise
        // collide with a unique-constraint violation. Same reasoning as the enumeration tests.
        await _factory.SeedAsync(async db =>
        {
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
        });
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
    }

    [Fact]
    public async Task Decisions_read_requires_no_admin_api_key()
    {
        // PublicRead, on ActivityEndpoint's settled precedent over this same store (#59 closed with
        // D2 unamended). If a later change gates this route, this fails loudly — which is the
        // intent, because the Suppressions surface reads it without sending a key.
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(DecisionsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Agreement_read_requires_no_admin_api_key()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(AgreementRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// THE TEST THE ENUMERATION SWEEP CANNOT WRITE. The sweep skips templated routes, so this is the
    /// only assertion that the review route is actually behind the admin gate. Named explicitly per
    /// the route's own doc comment.
    /// </summary>
    [Fact]
    public async Task Review_route_is_admin_gated()
    {
        await SeedAdminKeyAsync();
        var ids = await SeedDecisionsAsync(shadowModes: true);

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/admin/decisions/{ids[0]}/review",
            new ReviewDecisionRequest("agree", null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The gate must run BEFORE model binding. A bodiless request to this route must be refused for
    /// being unauthenticated (401), never for having no body (400) — a 400 here would mean an
    /// unauthenticated caller can tell a malformed body from a well-formed one and thereby probe
    /// past the gate. This is the leak that made the body optional; see the handler's own note.
    /// </summary>
    [Fact]
    public async Task Review_route_rejects_an_unkeyed_bodiless_request_at_the_gate_not_the_binder()
    {
        await SeedAdminKeyAsync();
        var ids = await SeedDecisionsAsync(shadowModes: false);

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/decisions/{ids[0]}/review");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Review_persists_the_verdict_and_the_note()
    {
        await SeedAdminKeyAsync();
        var ids = await SeedDecisionsAsync(shadowModes: true);

        using var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync(
            $"/api/admin/decisions/{ids[0]}/review",
            new ReviewDecisionRequest("disagree", "This one was a false positive."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reviewed = await response.Content.ReadFromJsonAsync<DecisionEntryResponse>();
        Assert.NotNull(reviewed);
        Assert.Equal("disagree", reviewed.ReviewVerdict);
        Assert.Equal("This one was a false positive.", reviewed.ReviewNote);
        Assert.NotNull(reviewed.ReviewedAt);
    }

    /// <summary>
    /// Plan §5: reviewing twice UPDATES, never duplicates. Asserted through the aggregate rather
    /// than by counting rows, because the aggregate is where a duplicate would actually do harm —
    /// one much-revisited decision counted twice silently inflates the agreement rate.
    /// </summary>
    [Fact]
    public async Task Reviewing_the_same_decision_twice_updates_rather_than_duplicating()
    {
        await SeedAdminKeyAsync();
        var ids = await SeedDecisionsAsync(shadowModes: true);
        var route = $"/api/admin/decisions/{ids[0]}/review";

        using var client = CreateAdminClient();

        await client.PostAsJsonAsync(route, new ReviewDecisionRequest("agree", "First call."));
        var second = await client.PostAsJsonAsync(route, new ReviewDecisionRequest("disagree", "Changed my mind."));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var reviewed = await second.Content.ReadFromJsonAsync<DecisionEntryResponse>();
        Assert.NotNull(reviewed);
        Assert.Equal("disagree", reviewed.ReviewVerdict);
        Assert.Equal("Changed my mind.", reviewed.ReviewNote);

        // The decision appears exactly once in the read, carrying only the latest verdict.
        var page = await client.GetFromJsonAsync<DecisionPageResponse>($"{DecisionsRoute}?limit=200");
        Assert.NotNull(page);
        Assert.Single(page.Decisions, d => d.Id == ids[0]);
    }

    [Fact]
    public async Task Review_rejects_an_unknown_verdict()
    {
        await SeedAdminKeyAsync();
        var ids = await SeedDecisionsAsync(shadowModes: true);

        using var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync(
            $"/api/admin/decisions/{ids[0]}/review",
            new ReviewDecisionRequest("maybe", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Review_of_an_unknown_decision_is_not_found()
    {
        await SeedAdminKeyAsync();

        using var client = CreateAdminClient();
        var response = await client.PostAsJsonAsync(
            "/api/admin/decisions/999999/review",
            new ReviewDecisionRequest("agree", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// AC1 / plan §5: the shadow-mode filter selects on the flag recorded AT DECISION TIME, so the
    /// two runs stay distinguishable. Seeded as one shadow-mode and one live decision on the same
    /// host; each filter must return only its own.
    /// </summary>
    [Fact]
    public async Task Shadow_mode_filter_selects_on_the_flag_recorded_at_decision_time()
    {
        await SeedAdminKeyAsync();
        var ids = await SeedDecisionsAsync(shadowModes: new[] { true, false });
        var shadowId = ids[0];
        var liveId = ids[1];

        using var client = _factory.CreateClient();

        var shadowPage = await client.GetFromJsonAsync<DecisionPageResponse>(
            $"{DecisionsRoute}?shadowMode=true&limit=200");
        var livePage = await client.GetFromJsonAsync<DecisionPageResponse>(
            $"{DecisionsRoute}?shadowMode=false&limit=200");

        Assert.NotNull(shadowPage);
        Assert.NotNull(livePage);

        Assert.Contains(shadowPage.Decisions, d => d.Id == shadowId);
        Assert.DoesNotContain(shadowPage.Decisions, d => d.Id == liveId);
        Assert.All(shadowPage.Decisions, d => Assert.True(d.ShadowMode));

        Assert.Contains(livePage.Decisions, d => d.Id == liveId);
        Assert.DoesNotContain(livePage.Decisions, d => d.Id == shadowId);
        Assert.All(livePage.Decisions, d => Assert.False(d.ShadowMode));
    }

    [Fact]
    public async Task Agreement_rejects_a_window_outside_the_retention_bound()
    {
        using var client = _factory.CreateClient();

        var tooLarge = await client.GetAsync(
            $"{AgreementRoute}?windowDays={DecisionReviewEndpoints.MaxAgreementWindowDays + 1}");
        var tooSmall = await client.GetAsync($"{AgreementRoute}?windowDays=0");

        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);
    }
}
