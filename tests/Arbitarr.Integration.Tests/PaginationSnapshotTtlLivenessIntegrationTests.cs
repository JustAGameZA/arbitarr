using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Search;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-8c/AC24: proves the query-snapshot TTL settings-liveness gap is closed end to end through
/// the real Host - a <c>QuerySnapshotTtl</c> setting changed via the admin API (<see
/// cref="AdminSettingsEndpointsTests"/>'s PUT route) must be visible to the next call the Host's
/// registered <see cref="ISnapshotTtlSource"/> makes, without a restart. This exercises the actual
/// Host-registered <c>SettingsSnapshotTtlSource</c> (scoped, backed by
/// <c>SettingsRepository</c>) rather than a fake, complementing the fake-based liveness coverage in
/// <c>Arbitarr.Api.Tests.PaginationSnapshotTtlLivenessTests</c>.
/// </summary>
public sealed class PaginationSnapshotTtlLivenessIntegrationTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string SettingsRoute = "/api/admin/settings";

    private readonly ArbitarrWebApplicationFactory _factory;

    public PaginationSnapshotTtlLivenessIntegrationTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PUT_query_snapshot_ttl_is_observed_by_the_next_ttl_source_read_without_a_restart()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);

        var newTtl = TimeSpan.FromSeconds(120);
        var response = await client.PutAsJsonAsync(
            $"{SettingsRoute}/{SettingKey.QuerySnapshotTtl}",
            new UpdateSettingRequest(newTtl.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Mirrors how PaginationSnapshotService's live-TTL ctor resolves its TTL source: a fresh
        // per-request scope.
        using var scope = _factory.Services.CreateScope();
        var ttlSource = scope.ServiceProvider.GetRequiredService<ISnapshotTtlSource>();
        var ttl = await ttlSource.GetAsync(CancellationToken.None);

        Assert.Equal(newTtl, ttl);
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
