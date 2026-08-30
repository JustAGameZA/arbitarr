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
/// D2 / M7-6: covers the admin API key gate end to end against the real Host — fail-closed on a
/// fresh install (no key configured yet), 401 on a wrong key, 200 on the correct key presented via
/// the <see cref="AdminApiKeyFilter.HeaderName"/> header, and that the gated route carries the
/// <see cref="RouteClassification.AdminMutating"/> classification.
/// </summary>
public sealed class AdminApiKeyGateTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string Route = "/api/admin/ping";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminApiKeyGateTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Fresh_install_with_no_admin_key_configured_fails_closed_with_503()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_admin_key_is_rejected_with_401()
    {
        await _factory.SeedAsync(db =>
        {
            db.Settings.Add(new SettingEntry
            {
                Name = SettingKey.AdminApiKey.ToString(),
                Value = "the-real-admin-key",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, "not-the-real-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_admin_key_header_is_rejected_with_401_when_a_key_is_configured()
    {
        await _factory.SeedAsync(db =>
        {
            db.Settings.Add(new SettingEntry
            {
                Name = SettingKey.AdminApiKey.ToString(),
                Value = "the-real-admin-key",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });

        using var client = _factory.CreateClient();

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Correct_admin_key_is_accepted_with_200()
    {
        await _factory.SeedAsync(db =>
        {
            db.Settings.Add(new SettingEntry
            {
                Name = SettingKey.AdminApiKey.ToString(),
                Value = "the-real-admin-key",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return Task.CompletedTask;
        });

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, "the-real-admin-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Admin_ping_route_is_classified_AdminMutating()
    {
        using var client = _factory.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(e => e.RoutePattern.RawText == Route);

        Assert.NotNull(endpoint);
        Assert.Equal(RouteClassification.AdminMutating, endpoint!.GetClassification());
    }
}
