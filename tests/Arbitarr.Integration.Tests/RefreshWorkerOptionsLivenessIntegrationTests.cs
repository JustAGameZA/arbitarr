using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-8b/AC24: proves the settings-liveness gap is closed end to end through the real Host - a
/// worker setting changed via the admin API (<see cref="AdminSettingsEndpointsTests"/>'s PUT route)
/// must be visible to the next call the worker makes against
/// <see cref="IRefreshWorkerOptionsSource"/>, without a restart. This exercises the actual
/// Host-registered <c>SettingsRefreshWorkerOptionsSource</c> (scoped, backed by
/// <c>SettingsRepository</c>) rather than a fake, complementing the fake-based liveness coverage in
/// <c>Arbitarr.Core.Tests.RefreshWorkerOptionsLivenessTests</c>.
/// </summary>
public sealed class RefreshWorkerOptionsLivenessIntegrationTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string SettingsRoute = "/api/admin/settings";

    private readonly ArbitarrWebApplicationFactory _factory;

    public RefreshWorkerOptionsLivenessIntegrationTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PUT_worker_cycle_interval_is_observed_by_the_next_options_read_without_a_restart()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);

        var newInterval = TimeSpan.FromSeconds(42);
        var response = await client.PutAsJsonAsync(
            $"{SettingsRoute}/{SettingKey.WorkerCycleInterval}",
            new UpdateSettingRequest(newInterval.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Mirrors how RefreshWorker's scope-factory ctor resolves options: a fresh scope per cycle.
        using var scope = _factory.Services.CreateScope();
        var optionsSource = scope.ServiceProvider.GetRequiredService<IRefreshWorkerOptionsSource>();
        var options = await optionsSource.GetAsync(CancellationToken.None);

        Assert.Equal(newInterval, options.WorkerCycleInterval);
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
