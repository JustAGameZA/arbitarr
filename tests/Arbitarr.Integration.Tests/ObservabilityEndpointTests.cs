using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7 step 7: <c>GET /api/admin/observability</c> is admin-gated (D2) and reports the live
/// <see cref="ObservabilityCounters"/> snapshot plus metadata-cache coverage read from the store.
/// </summary>
public sealed class ObservabilityEndpointTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "observability-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public ObservabilityEndpointTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Rejects_requests_without_the_admin_key()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/admin/observability");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reports_live_counters_and_metadata_cache_coverage()
    {
        await SeedAdminKeyAsync();
        await _factory.SeedAsync(async db =>
        {
            db.MetadataCacheEntries.RemoveRange(db.MetadataCacheEntries);
            await db.SaveChangesAsync();
            db.MetadataCacheEntries.AddRange(
                Entry("tvdb:1", "xem", isNegative: false),
                Entry("tvdb:1", "anime-lists", isNegative: true),
                Entry("tvdb:2", "xem", isNegative: false));
        });

        var counters = _factory.Services.GetRequiredService<ObservabilityCounters>();
        counters.RecordResultsIn(10);
        counters.RecordSuppressed("DenyRule", "no-cam");
        counters.RecordVerdictCacheLookup(hit: true);
        counters.RecordVerdictCacheLookup(hit: false);
        counters.RecordLlmCall(failed: true);
        counters.RecordSearchCacheHit(CacheBand.Fresh, TimeSpan.FromMinutes(2));

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);

        using var response = await client.GetAsync("/api/admin/observability");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var live = body.GetProperty("counters");
        Assert.True(live.GetProperty("resultsIn").GetInt64() >= 10);
        Assert.True(live.GetProperty("suppressedBySourceAndReason").GetProperty("DenyRule:no-cam").GetInt64() >= 1);
        Assert.True(live.GetProperty("llmFailures").GetInt64() >= 1);
        Assert.True(live.GetProperty("verdictCache").GetProperty("hits").GetInt64() >= 1);
        Assert.True(live.GetProperty("searchCache").GetProperty("freshHits").GetInt64() >= 1);
        Assert.True(live.GetProperty("servedAgeDistribution").GetProperty("1m-5m").GetInt64() >= 1);

        var coverage = body.GetProperty("metadataCache");
        Assert.Equal(3, coverage.GetProperty("entries").GetInt64());
        Assert.Equal(1, coverage.GetProperty("negativeEntries").GetInt64());
        Assert.Equal(2, coverage.GetProperty("distinctSeries").GetInt64());
    }

    private static MetadataCacheEntry Entry(string seriesKey, string source, bool isNegative) => new()
    {
        SeriesKey = seriesKey,
        Source = source,
        PayloadJson = "{}",
        SourceSnapshotVersion = "v1",
        IsNegative = isNegative,
        FetchedAt = DateTimeOffset.UtcNow,
        RefreshAfter = DateTimeOffset.UtcNow.AddHours(1),
    };

    private async Task SeedAdminKeyAsync()
    {
        await _factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry { Name = SettingKey.AdminApiKey.ToString(), Value = AdminKey, UpdatedAt = DateTimeOffset.UtcNow });
            }
            else
            {
                existing.Value = AdminKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
    }
}
