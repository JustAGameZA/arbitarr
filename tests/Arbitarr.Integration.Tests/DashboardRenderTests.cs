using System.Net;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M2-1 (page-load half): <c>GET /</c> serves the dashboard shell with all three panel containers
/// present, and its script/style assets are reachable, so the three-panel layout
/// (src/Arbitarr.Host/wwwroot/index.html + app.js) is actually wired to the real static-file
/// pipeline rather than only existing on disk.
/// </summary>
public sealed class DashboardRenderTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public DashboardRenderTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Root_document_contains_all_three_dashboard_panels()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("id=\"status-panel\"", html);
        Assert.Contains("id=\"searches-panel\"", html);
        Assert.Contains("id=\"config-panel\"", html);
        Assert.Contains("app.js", html);
        Assert.Contains("style.css", html);
    }

    [Theory]
    [InlineData("/app.js")]
    [InlineData("/style.css")]
    public async Task Dashboard_static_assets_are_served(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_script_fetches_all_three_endpoints()
    {
        using var client = _factory.CreateClient();

        var script = await client.GetStringAsync("/app.js");

        Assert.Contains("/api/status", script);
        Assert.Contains("/api/searches/recent", script);
        Assert.Contains("/api/config/effective", script);
    }
}
