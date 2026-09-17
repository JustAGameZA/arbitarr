using System.Net;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-mhd2: the gating tests for <c>GET /api/admin/status/diagnostics</c>, the admin-gated read the
/// source and worker error detail moved to when <c>GET /api/status</c> converged on publishing a
/// closed outcome only.
///
/// <para><b>Why these exist even though the route is non-templated.</b>
/// <c>AdminApiKeyRouteEnumerationTests</c> sweeps every registered admin route and would cover this
/// one by construction — the route was deliberately chosen without a <c>{</c> segment precisely so
/// it could not fall into that sweep's templated-route skip (CLAUDE.md §2). But the sweep passing is
/// evidence about the sweep's route set, not about this route's behaviour on each status, so the
/// by-name cases are asserted here too. If the route is ever made templated, the sweep silently
/// stops covering it and these become the only coverage.</para>
///
/// <para><b>No body is sent on any request here, on purpose.</b> A required body is rejected BEFORE
/// <see cref="AdminApiKeyFilter"/> runs, which leaks 400-vs-503 and tells an unauthenticated caller
/// whether a route exists. This is a GET with no body binding at all, and these requests carry none
/// so that the fail-closed status below is the filter's and not the model binder's.</para>
/// </summary>
public sealed class AdminStatusDiagnosticsEndpointTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string Route = "/api/admin/status/diagnostics";
    private const string RealKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminStatusDiagnosticsEndpointTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Its OWN factory, not the class fixture's. The fixture's SQLite database is shared by every
    /// [Fact] here, and the other cases seed an admin key into it — so run against the shared one
    /// this would assert "fresh install" against a database that another test had already
    /// configured, and would pass or fail on execution order rather than on behaviour.
    /// </summary>
    [Fact]
    public async Task Fresh_install_with_no_admin_key_configured_fails_closed_with_503()
    {
        using var freshFactory = new ArbitarrWebApplicationFactory();
        using var client = freshFactory.CreateClient();

        // No body, no header: exactly the shape an unauthenticated prober would send.
        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Missing_admin_key_header_is_rejected_with_401_when_a_key_is_configured()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // The gate refused before any handler ran, so no detail travelled with the refusal.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("lastError", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sources", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_admin_key_is_rejected_with_401()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, "not-the-real-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The positive control for the three refusals above: the SAME route with the SAME shape of
    /// request answers 200 once the correct key is presented. Without this, all three could pass
    /// against a route that was simply broken or unregistered.
    /// </summary>
    [Fact]
    public async Task Correct_admin_key_is_accepted_with_200_and_returns_the_diagnostics_shape()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, RealKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The shape is present even with nothing to report, so the dashboard's optional read has a
        // well-formed body to merge rather than having to special-case an empty deployment.
        Assert.Contains("sources", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("worker", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classification is by path prefix, never by HTTP verb (CLAUDE.md §2). This route is a READ and
    /// is nonetheless <c>AdminMutating</c>, which is the classification that carries the key
    /// requirement — a reviewer reading "AdminMutating" on a GET should find this test explaining it
    /// rather than concluding it is a mistake and "fixing" it to PublicRead.
    /// </summary>
    [Fact]
    public void The_route_is_classified_AdminMutating_despite_being_a_read()
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(e => e.RoutePattern.RawText == Route);

        Assert.NotNull(endpoint);
        Assert.Equal(RouteClassification.AdminMutating, endpoint!.GetClassification());
    }

    /// <summary>
    /// The route carries no <c>{</c> segment. That is load-bearing rather than cosmetic: the
    /// enumeration sweep skips every templated route, so a later change to
    /// <c>/api/admin/status/diagnostics/{sourceName}</c> would quietly drop this route out of that
    /// sweep's coverage. This fails first and says so.
    /// </summary>
    [Fact]
    public void The_route_is_non_templated_so_the_enumeration_sweep_covers_it()
    {
        Assert.DoesNotContain('{', Route);
    }

    // Upsert rather than Add: the factory's SQLite database is shared across every [Fact] in this
    // IClassFixture-scoped class (Name is the SettingEntry primary key), so a second test seeding
    // the same key would collide with a unique-constraint violation instead of overwriting.
    private async Task SeedAdminKeyAsync()
    {
        await _factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = RealKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = RealKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
    }
}
