using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Arbitarr.Data.Entities;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #42: <c>/api/config/effective</c> must report the live shadow-mode value rather than a hardcoded
/// null. This lives in its own <see cref="ArbitarrWebApplicationFactory"/> instance (a separate test
/// class gets a fresh factory/database from xUnit) rather than alongside
/// <see cref="DashboardReadOnlyTests"/>, because that class's "no rows persisted" test depends on an
/// empty Settings table and xUnit does not guarantee execution order within a class sharing one
/// <see cref="IClassFixture{TFixture}"/> instance.
/// </summary>
public sealed class EffectiveConfigShadowModeTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public EffectiveConfigShadowModeTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Effective_config_endpoint_reports_false_when_shadow_mode_explicitly_disabled()
    {
        await _factory.SeedAsync(db =>
        {
            db.Settings.Add(new SettingEntry { Name = "ShadowMode", Value = "false" });
            return Task.CompletedTask;
        });

        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<EffectiveConfigResponse>("/api/config/effective");

        Assert.NotNull(response);
        Assert.NotNull(response!.ShadowMode);
        Assert.False(response.ShadowMode);
    }
}
