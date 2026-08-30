using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Covers the M2 dashboard read-only surface end to end against the real Host: seeded-row
/// rendering (M2-1), the 405 mutation guard (M2-2), circuit-open reporting as unhealthy rather than
/// blanket "ok" (M2-4), no API key required at this milestone (M2-5), no ad-hoc search route exists
/// (M2-6), and worker status is restricted to its documented enumerated values (M2-7).
/// </summary>
public sealed class DashboardReadOnlyTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public DashboardReadOnlyTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Status_endpoint_reports_seeded_source_health_rows()
    {
        await _factory.SeedAsync(db =>
        {
            db.SourceHealthRecords.Add(new SourceHealthRecord
            {
                SourceName = "nzbhydra2",
                State = CircuitBreakerState.Closed,
                ConsecutiveFailures = 0,
            });
            return Task.CompletedTask;
        });

        using var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<StatusResponse>("/api/status");

        Assert.NotNull(response);
        var source = Assert.Single(response!.Sources);
        Assert.Equal("nzbhydra2", source.SourceName);
        Assert.Equal("closed", source.State);
    }

    [Fact]
    public async Task Status_endpoint_reports_open_circuit_as_unhealthy_not_ok()
    {
        await _factory.SeedAsync(db =>
        {
            db.SourceHealthRecords.Add(new SourceHealthRecord
            {
                SourceName = "flaky-source",
                State = CircuitBreakerState.Open,
                ConsecutiveFailures = 7,
                LastError = "connection refused",
            });
            return Task.CompletedTask;
        });

        using var client = _factory.CreateClient();
        var response = await client.GetFromJsonAsync<StatusResponse>("/api/status");

        var source = Assert.Single(response!.Sources, s => s.SourceName == "flaky-source");
        Assert.Equal("open", source.State);
        Assert.NotEqual("ok", source.State);
        Assert.NotEqual("closed", source.State);
    }

    [Fact]
    public async Task Recent_searches_endpoint_reports_seeded_ring_buffer_entries()
    {
        using var client = _factory.CreateClient();
        var log = _factory.Services.GetRequiredService<RecentSearchLog>();
        log.Record(new RecentSearchEntry(
            ReceivedAt: DateTimeOffset.UtcNow,
            Query: "the.wire.s01",
            ResolvedIdentity: "The Wire",
            ResultCount: 3,
            ElapsedMilliseconds: 42.5,
            Band: null));

        var response = await client.GetFromJsonAsync<List<RecentSearchEntry>>("/api/searches/recent");

        Assert.NotNull(response);
        var entry = Assert.Single(response!);
        Assert.Equal("the.wire.s01", entry.Query);
        Assert.Equal("The Wire", entry.ResolvedIdentity);
    }

    [Fact]
    public async Task Effective_config_endpoint_reports_default_settings_when_no_rows_persisted()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<EffectiveConfigResponse>("/api/config/effective");

        Assert.NotNull(response);
        Assert.False(response!.NzbHydraConfigured);
        Assert.True(response.WorkerEnabled);
        Assert.Null(response.ShadowMode);
    }

    [Fact]
    public async Task Status_worker_status_is_restricted_to_documented_enumerated_values_before_M3()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<StatusResponse>("/api/status");

        // M2-7: before M3 implements the proactive-refresh worker, WorkerStatus must never read as
        // healthy/running — "not-implemented" is the only value StatusEndpoint may emit right now.
        Assert.Equal(StatusEndpoint.WorkerNotImplemented, response!.WorkerStatus);
        Assert.DoesNotContain(response.WorkerStatus, new[] { "ok", "healthy", "running" });
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/searches/recent")]
    [InlineData("/api/config/effective")]
    public async Task Dashboard_routes_require_no_api_key(string route)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(route);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task No_ad_hoc_search_route_exists()
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var routes = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToArray();

        // D1: 7-lite is dashboard + recent-searches log + read-only config viewer only — no
        // interactive/ad-hoc search trigger for the *unauthenticated* dashboard surface. M7-1 adds
        // exactly one exception: an admin-gated (D2, AdminApiKeyFilter) ad-hoc search route for the
        // admin-only dashboard extension — it is not reachable without the admin API key, so it does
        // not weaken D1's read-only/no-auth guarantee for the public 7-lite routes. Any other
        // "search"-named surface remains disallowed.
        Assert.DoesNotContain(routes, r => r is not null && r.Contains("search", StringComparison.OrdinalIgnoreCase)
            && r != "/api/searches/recent" && r != "/api/admin/search");
    }

    public static IEnumerable<object[]> DashboardRoutesAndDisallowedMethods()
    {
        string[] routes = ["/api/status", "/api/searches/recent", "/api/config/effective"];
        string[] mutatingMethods = ["POST", "PUT", "PATCH", "DELETE"];

        foreach (var route in routes)
        {
            foreach (var method in mutatingMethods)
            {
                yield return [route, method];
            }
        }
    }

    [Theory]
    [MemberData(nameof(DashboardRoutesAndDisallowedMethods))]
    public async Task Dashboard_routes_reject_mutating_verbs_with_405(string route, string method)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
