using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #43: the dedicated admin-key write route, and the properties that keep it from re-opening the
/// hole the catalog's exclusion of this key exists to prevent — it must persist a valid key, reject
/// a weak one via the existing validator, and never return the value on any surface.
/// </summary>
public sealed class AdminSecurityEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string SeededKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminSecurityEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PUT_admin_key_persists_a_valid_key_and_returns_no_content()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        const string newKey = "0123456789abcdef-rotated";

        var response = await client.PutAsJsonAsync(
            AdminSecurityEndpoints.AdminKeyRoute,
            new UpdateAdminApiKeyRequest(newKey));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // 204 carries no body, so not even an echo of the accepted key leaves the process.
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());

        // The rotated key is live: the old one no longer opens the gate.
        using var staleClient = _factory.CreateClient();
        staleClient.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, SeededKey);
        var stale = await staleClient.GetAsync("/api/admin/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        // Restore the shared fixture's key so sibling [Fact]s are unaffected by ordering.
        await SeedAdminKeyAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // 15 characters — one below SettingsValidator.ValidateAdminApiKey's floor.
    [InlineData("0123456789abcde")]
    public async Task PUT_admin_key_rejects_a_value_that_fails_the_validator(string proposed)
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        var response = await client.PutAsJsonAsync(
            AdminSecurityEndpoints.AdminKeyRoute,
            new UpdateAdminApiKeyRequest(proposed));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Rejected means not persisted: the previously seeded key still works.
        using var stillValid = AuthorizedClient();
        var probe = await stillValid.GetAsync("/api/admin/settings");
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
    }

    [Fact]
    public async Task PUT_admin_key_is_itself_gated_by_the_admin_key()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            AdminSecurityEndpoints.AdminKeyRoute,
            new UpdateAdminApiKeyRequest("0123456789abcdef"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_admin_key_is_never_readable_through_the_settings_surface()
    {
        // Step 2 of the plan: a regression test pinning a property that already holds, so the new
        // write route cannot quietly acquire a read side later. GET enumerates SettingsCatalog,
        // which deliberately excludes this key.
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        var response = await client.GetAsync("/api/admin/settings");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(SeededKey, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(SettingKey.AdminApiKey.ToString(), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalog_write_path_still_refuses_the_admin_key_with_404()
    {
        // The repository now accepts this key, so the 404 must come from the catalog allow-list in
        // AdminSettingsEndpoints rather than from the repository throw #43 removed. If someone adds
        // the key to SettingsCatalog to "make it editable", this fails — which is the point.
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/settings/{SettingKey.AdminApiKey}",
            new UpdateSettingRequest("0123456789abcdef"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, SeededKey);
        return client;
    }

    // Upsert rather than Add: the factory's SQLite database is shared across every [Fact] in this
    // IClassFixture-scoped class (Name is the SettingEntry primary key).
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
                    Value = SeededKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = SeededKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
    }
}
