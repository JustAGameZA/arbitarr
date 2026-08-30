using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Wave-C item 3 (plan M7 UI list item 3, P3): covers the admin-gated suppressed/de-ranked view
/// against the real Host — reading straight from <see cref="SuppressionAuditLogEntry"/> (already
/// written by <see cref="Arbitarr.Api.Search.FilterStage"/>, M4-5), attributing each row to the
/// layer that acted, and honoring the admin gate in both directions.
/// </summary>
public sealed class SuppressionViewEndpointTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string SuppressionsRoute = "/api/admin/suppressions";

    private readonly ArbitarrWebApplicationFactory _factory;

    public SuppressionViewEndpointTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GET_suppressions_requires_the_admin_key()
    {
        // Seed the key first so this deterministically exercises the "wrong/missing key" (401) path
        // rather than the "no key configured yet" (503) fail-closed path — the two are both real
        // AdminApiKeyFilter outcomes and which one an unseeded request hits depends on whether another
        // [Fact] in this IClassFixture-scoped class has already configured a key (xUnit does not
        // guarantee in-class execution order), so the precondition must be pinned explicitly here.
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        var response = await client.GetAsync(SuppressionsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_suppressions_succeeds_with_the_admin_key()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync(SuppressionsRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GET_suppressions_attributes_each_row_to_the_layer_that_acted()
    {
        await SeedAdminKeyAsync();
        var queryKey = $"query-{Guid.NewGuid():N}";

        await _factory.SeedAsync(db =>
        {
            db.SuppressionAuditLogEntries.AddRange(
                new SuppressionAuditLogEntry
                {
                    OccurredAt = DateTimeOffset.UtcNow,
                    ReleaseIdentifier = "release-deny",
                    QueryKey = queryKey,
                    RuleName = "block-cam-rips",
                    Reason = "Suppressed by DenyRule 'block-cam-rips' (profile 'default', query 'the.wire').",
                    ShadowMode = false,
                },
                new SuppressionAuditLogEntry
                {
                    OccurredAt = DateTimeOffset.UtcNow,
                    ReleaseIdentifier = "release-ai",
                    QueryKey = queryKey,
                    RuleName = "ai",
                    Reason = "Suppressed by Ai (profile 'default', query 'the.wire').",
                    ShadowMode = true,
                });
            return Task.CompletedTask;
        });

        using var client = AuthorizedClient();
        var response = await client.GetAsync($"{SuppressionsRoute}?queryKey={queryKey}");
        response.EnsureSuccessStatusCode();

        var entries = await response.Content.ReadFromJsonAsync<List<SuppressionViewEntryResponse>>();

        Assert.NotNull(entries);
        Assert.Equal(2, entries!.Count);

        var denyEntry = Assert.Single(entries, e => e.ReleaseIdentifier == "release-deny");
        Assert.Equal("block-cam-rips", denyEntry.Layer);
        Assert.False(denyEntry.ShadowMode);
        Assert.Contains("DenyRule", denyEntry.Reason);

        var aiEntry = Assert.Single(entries, e => e.ReleaseIdentifier == "release-ai");
        Assert.Equal("ai", aiEntry.Layer);
        Assert.True(aiEntry.ShadowMode);
        Assert.Contains("Ai", aiEntry.Reason);
    }

    [Fact]
    public async Task GET_suppressions_returns_an_empty_list_for_a_query_with_no_suppressions()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync($"{SuppressionsRoute}?queryKey=no-such-query-{Guid.NewGuid():N}");
        response.EnsureSuccessStatusCode();

        var entries = await response.Content.ReadFromJsonAsync<List<SuppressionViewEntryResponse>>();

        Assert.NotNull(entries);
        Assert.Empty(entries!);
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
