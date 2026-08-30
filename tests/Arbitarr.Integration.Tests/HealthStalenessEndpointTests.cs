using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-8 / AC25: covers <c>GET /api/health/staleness</c> against the real Host — default settings
/// produce the documented default envelope, the route needs no admin key (it is read-only, not
/// admin-mutating), and mutating verbs are rejected.
/// </summary>
public sealed class HealthStalenessEndpointTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string Route = "/api/health/staleness";

    private readonly ArbitarrWebApplicationFactory _factory;

    public HealthStalenessEndpointTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Reports_default_envelope_when_no_settings_persisted()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<StalenessEnvelopeResponse>(Route);

        Assert.NotNull(response);
        // Defaults: fresh_until = 15m, classifier_queue_latency = 0 (no AI classifier merged yet) ->
        // worst_case_unjudged_age = 15m + 0 = 15m.
        Assert.Equal(TimeSpan.FromMinutes(15).ToString(), response!.fresh_until);
        Assert.Equal(TimeSpan.FromMinutes(15).ToString(), response.search_result_cache_band_bound);
        Assert.Equal(TimeSpan.Zero.ToString(), response.classifier_queue_latency);
        Assert.Equal(TimeSpan.FromMinutes(15).ToString(), response.worst_case_unjudged_age);
        Assert.Equal(TimeSpan.FromDays(7).ToString(), response.serve_until);
    }

    [Fact]
    public async Task Route_requires_no_admin_api_key()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Route_rejects_mutating_verbs_with_405()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
