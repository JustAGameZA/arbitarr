using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-5: covers the admin settings surface end to end against the real Host — both endpoints are
/// admin-gated (D2), <c>GET</c> returns the full catalog with current values and AC24 rationale
/// text, and <c>PUT</c> validates via <see cref="SettingsValidator"/> before persisting, rejecting
/// out-of-bounds values with 400 rather than clamping.
/// </summary>
public sealed class AdminSettingsEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string SettingsRoute = "/api/admin/settings";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminSettingsEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GET_settings_requires_the_admin_key()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(SettingsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_settings_returns_the_full_catalog_with_rationale_and_current_values()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync(SettingsRoute);
        response.EnsureSuccessStatusCode();

        var entries = await response.Content.ReadFromJsonAsync<List<SettingCatalogEntryResponse>>();

        Assert.NotNull(entries);
        Assert.Equal(SettingsCatalog.Entries.Count, entries!.Count);
        Assert.DoesNotContain(entries, e => e.Key == SettingKey.AdminApiKey.ToString());
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Rationale)));

        var freshUntil = entries.Single(e => e.Key == nameof(SettingKey.FreshUntil));
        Assert.Equal(TimeSpan.FromMinutes(15).ToString(), freshUntil.Value);
    }

    [Fact]
    public async Task GET_settings_reports_bounds_and_the_maintenance_interval_requires_restart()
    {
        // AC24/M7-8: the admin settings UI renders each setting's floor/ceiling from this payload
        // rather than hardcoding them in JS, and must be able to show a "requires restart" badge.
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync(SettingsRoute);
        response.EnsureSuccessStatusCode();

        var entries = await response.Content.ReadFromJsonAsync<List<SettingCatalogEntryResponse>>();
        Assert.NotNull(entries);

        var workerCycleInterval = entries!.Single(e => e.Key == nameof(SettingKey.WorkerCycleInterval));
        Assert.Equal(TimeSpan.FromSeconds(15).ToString(), workerCycleInterval.Min);
        Assert.NotNull(workerCycleInterval.Max);
        Assert.False(workerCycleInterval.RequiresRestart);

        var aiVerdictCacheTtl = entries.Single(e => e.Key == nameof(SettingKey.AiVerdictCacheTtl));
        Assert.Equal(TimeSpan.FromHours(24).ToString(), aiVerdictCacheTtl.Min);
        Assert.Null(aiVerdictCacheTtl.Max);

        var workerEnabled = entries.Single(e => e.Key == nameof(SettingKey.WorkerEnabled));
        Assert.Null(workerEnabled.Min);
        Assert.Null(workerEnabled.Max);

        var maintenanceJobInterval = entries.Single(e => e.Key == nameof(SettingKey.MaintenanceJobInterval));
        Assert.Equal(TimeSpan.FromMinutes(5).ToString(), maintenanceJobInterval.Min);
        Assert.Equal(TimeSpan.FromHours(24).ToString(), maintenanceJobInterval.Max);
        Assert.True(maintenanceJobInterval.RequiresRestart);
    }

    [Fact]
    public async Task PUT_settings_persists_a_valid_value()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.PutAsJsonAsync(
            $"{SettingsRoute}/{SettingKey.WorkerCycleInterval}",
            new UpdateSettingRequest(TimeSpan.FromSeconds(30).ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var getResponse = await client.GetAsync(SettingsRoute);
        var entries = await getResponse.Content.ReadFromJsonAsync<List<SettingCatalogEntryResponse>>();
        var updated = entries!.Single(e => e.Key == nameof(SettingKey.WorkerCycleInterval));
        Assert.Equal(TimeSpan.FromSeconds(30).ToString(), updated.Value);
    }

    [Fact]
    public async Task PUT_settings_rejects_an_out_of_bounds_value_with_400()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        // Floor is 15s (SettingsValidator.ValidateWorkerCycleInterval).
        var response = await client.PutAsJsonAsync(
            $"{SettingsRoute}/{SettingKey.WorkerCycleInterval}",
            new UpdateSettingRequest(TimeSpan.FromSeconds(1).ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PUT_settings_rejects_the_admin_api_key_itself_with_404()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        var response = await client.PutAsJsonAsync(
            $"{SettingsRoute}/{SettingKey.AdminApiKey}",
            new UpdateSettingRequest("attempted-override"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PUT_settings_without_admin_key_is_rejected_with_401()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"{SettingsRoute}/{SettingKey.WorkerCycleInterval}",
            new UpdateSettingRequest(TimeSpan.FromSeconds(30).ToString()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/admin-settings.html")]
    [InlineData("/admin-settings.js")]
    public async Task Admin_settings_static_assets_are_served(string path)
    {
        // M7-8: the admin settings page (wwwroot/admin-settings.html + .js) must actually be wired
        // to the static-file pipeline, not merely exist on disk.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
    }

    // Upsert rather than Add: ArbitarrWebApplicationFactory's SQLite database is shared across
    // every [Fact] in this IClassFixture-scoped test class (Name is the SettingEntry primary key).
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
}
