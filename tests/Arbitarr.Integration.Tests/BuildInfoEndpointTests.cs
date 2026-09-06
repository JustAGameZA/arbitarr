using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Routing;
using Arbitarr.Api.SystemInfo;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Issue #46/R1: covers <c>GET /api/system/build</c> against the real Host — all five fields are
/// reported, no admin key is required (AC1/AC4), and with no build args supplied (the test host's
/// own build) every unset build-time field renders the explicit "unknown (local build)" marker
/// rather than blank (AC2). Also covers <c>/health</c>'s corrected version field and unchanged shape
/// (AC3) — the regression guard against the liveness probe growing fields.
/// </summary>
public sealed class BuildInfoEndpointTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string Route = "/api/system/build";

    private readonly ArbitarrWebApplicationFactory _factory;

    public BuildInfoEndpointTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Reports_all_five_fields()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<BuildInfoResponse>(Route);

        Assert.NotNull(response);
        Assert.NotNull(response!.CommitSha);
        Assert.NotNull(response.ImageTag);
        Assert.NotNull(response.BuildTimestampUtc);
        Assert.NotNull(response.InformationalVersion);
        Assert.True(response.UptimeSeconds >= 0);
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
    public async Task Is_classified_PublicRead()
    {
        using var client = _factory.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(e => e.RoutePattern.RawText == Route);

        Assert.NotNull(endpoint);
        Assert.Equal(RouteClassification.PublicRead, endpoint!.GetClassification());
    }

    [Fact]
    public async Task Unset_build_time_fields_render_the_explicit_unknown_marker()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<BuildInfoResponse>(Route);

        // The test host is built with no CommitSha/ImageTag/BuildTimestampUtc MSBuild properties
        // supplied (a plain `dotnet test`), so every build-time field must fall back to the
        // explicit marker rather than an empty string -- a blank value here would look like a
        // bug rather than a known, correctly-detected local build.
        Assert.NotNull(response);
        Assert.Equal(BuildInfo.UnknownLocalBuild, response!.CommitSha);
        Assert.Equal(BuildInfo.UnknownLocalBuild, response.ImageTag);
        Assert.Equal(BuildInfo.UnknownLocalBuild, response.BuildTimestampUtc);
    }

    [Fact]
    public async Task Route_rejects_mutating_verbs_with_405()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_the_informational_version_and_its_shape_is_otherwise_unchanged()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.NotNull(response);
        Assert.Equal("ok", response!.Status);
        Assert.Equal("Arbitarr", response.Name);
        Assert.NotNull(response.Version);
        Assert.NotEqual("1.0.0", response.Version);

        // Regression guard: /health is a liveness probe, not a build-info surface. Deserializing
        // strictly into this three-field record means any additional field the probe grew would
        // still round-trip here (System.Text.Json ignores unknown members by default), so this
        // alone would not catch an addition -- the raw-JSON key-count assertion below is what
        // actually would.
        var raw = await client.GetStringAsync("/health");
        using var document = System.Text.Json.JsonDocument.Parse(raw);
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
    }

    private sealed record HealthResponse(string Status, string Name, string Version);
}
