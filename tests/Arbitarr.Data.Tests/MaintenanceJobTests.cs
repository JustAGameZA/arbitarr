using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Data.Tests;

/// <summary>
/// Proves the maintenance job's search-result cache prune predicate is exactly
/// <c>age &gt; serve_until</c> against a real SQLite database (not just the pure predicate unit
/// tests in Arbitarr.Core.Tests) — specifically that a row well past fresh_until but still
/// within serve_until survives a maintenance run (plan lines ~1058-1080; the D3 anti-pattern this
/// job must not fall into).
/// </summary>
public sealed class MaintenanceJobTests : IDisposable
{
    private readonly string _dbPath;
    private readonly FakeTimeProvider _timeProvider;
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public MaintenanceJobTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-maintenance-test-{Guid.NewGuid():N}.db");
        _timeProvider = new FakeTimeProvider(Now);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    private static SettingsSnapshot Settings(TimeSpan serveUntil) => SettingsSnapshot.Defaults(TimeSpan.FromMinutes(15)) with
    {
        ServeUntil = serveUntil,
    };

    [Fact]
    public async Task RunAsync_PrunesSearchResultCacheRow_PastServeUntil()
    {
        var serveUntil = TimeSpan.FromDays(7);

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.SearchResultCacheEntries.Add(new SearchResultCacheEntry
            {
                QueryKey = "expired-query",
                PayloadJson = "[]",
                FetchedAt = Now - serveUntil - TimeSpan.FromSeconds(1),
                FreshUntil = Now - TimeSpan.FromDays(6),
                ServeUntil = Now - TimeSpan.FromSeconds(1),
                LastRequestedAt = Now - serveUntil - TimeSpan.FromSeconds(1),
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(serveUntil));
            Assert.Equal(1, result.SearchResultCacheRowsPruned);
        }

        using (var context = CreateContext())
        {
            Assert.Empty(context.SearchResultCacheEntries);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotPruneSearchResultCacheRow_WellPastFreshUntilButWithinServeUntil()
    {
        // Anti-conflation guard against the D3 anti-pattern: a row six days old at the 7-day
        // serve_until default is far past any reasonable fresh_until, but is still legitimately
        // valid data and must survive a maintenance run.
        var freshUntil = TimeSpan.FromMinutes(15);
        var serveUntil = TimeSpan.FromDays(7);
        var age = freshUntil + TimeSpan.FromDays(6);

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.SearchResultCacheEntries.Add(new SearchResultCacheEntry
            {
                QueryKey = "still-valid-query",
                PayloadJson = "[]",
                FetchedAt = Now - age,
                FreshUntil = Now - age + freshUntil,
                ServeUntil = Now - age + serveUntil,
                LastRequestedAt = Now - age,
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(serveUntil));
            Assert.Equal(0, result.SearchResultCacheRowsPruned);
        }

        using (var context = CreateContext())
        {
            Assert.Single(context.SearchResultCacheEntries);
        }
    }

    [Fact]
    public async Task RunAsync_PrunesSuppressionAuditLogRow_PastRetention()
    {
        var retention = TimeSpan.FromDays(30);

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.SuppressionAuditLogEntries.Add(new SuppressionAuditLogEntry
            {
                OccurredAt = Now - retention - TimeSpan.FromSeconds(1),
                ReleaseIdentifier = "release-1",
                QueryKey = "query-1",
                RuleName = "rule-1",
                Reason = "test",
                ShadowMode = false,
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));
            Assert.Equal(1, result.SuppressionAuditLogRowsPruned);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotPruneSuppressionAuditLogRow_WithinRetention()
    {
        var retention = TimeSpan.FromDays(30);

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.SuppressionAuditLogEntries.Add(new SuppressionAuditLogEntry
            {
                OccurredAt = Now - retention + TimeSpan.FromDays(1),
                ReleaseIdentifier = "release-2",
                QueryKey = "query-2",
                RuleName = "rule-2",
                Reason = "test",
                ShadowMode = false,
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));
            Assert.Equal(0, result.SuppressionAuditLogRowsPruned);
        }
    }

    private static SettingsSnapshot SettingsWithAiVerdictCache(TimeSpan ttl, int rowCeiling) =>
        SettingsSnapshot.Defaults(TimeSpan.FromMinutes(15)) with
        {
            AiVerdictCacheTtl = ttl,
            AiVerdictCacheRowCeiling = rowCeiling,
        };

    [Fact]
    public async Task RunAsync_PrunesAiVerdictCacheRows_OverRowCeiling_EvictsOldestLastAccessedAt()
    {
        // M5 security review (MED): the row-ceiling LRU trim must evict the coldest
        // (oldest-LastAccessedAt) rows first, regardless of TTL, so an unbounded stream of distinct
        // releases cannot grow this table without limit even when accessed faster than TTL expiry.
        var ttl = TimeSpan.FromDays(30);

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            for (var i = 0; i < 5; i++)
            {
                context.VerdictCacheEntries.Add(new VerdictCacheEntry
                {
                    ReleaseKeyHash = $"hash-{i}",
                    Verdict = 1,
                    Confidence = 0.9,
                    ModelName = "model-a",
                    ModelDigest = "digest-1",
                    PromptVersion = "v1",
                    CreatedAt = Now - TimeSpan.FromMinutes(10 - i),
                    LastAccessedAt = Now - TimeSpan.FromMinutes(10 - i), // entry 0 is oldest, entry 4 is newest
                });
            }
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(SettingsWithAiVerdictCache(ttl, rowCeiling: 3));
            Assert.Equal(2, result.AiVerdictCacheRowsPruned);
        }

        using (var context = CreateContext())
        {
            var survivingHashes = context.VerdictCacheEntries.Select(e => e.ReleaseKeyHash).ToList();
            Assert.Equal(3, survivingHashes.Count);
            Assert.DoesNotContain("hash-0", survivingHashes);
            Assert.DoesNotContain("hash-1", survivingHashes);
            Assert.Contains("hash-2", survivingHashes);
            Assert.Contains("hash-3", survivingHashes);
            Assert.Contains("hash-4", survivingHashes);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotPruneAiVerdictCacheRows_UnderRowCeilingAndWithinTtl()
    {
        var ttl = TimeSpan.FromDays(30);

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.VerdictCacheEntries.Add(new VerdictCacheEntry
            {
                ReleaseKeyHash = "hash-only",
                Verdict = 1,
                Confidence = 0.9,
                ModelName = "model-a",
                ModelDigest = "digest-1",
                PromptVersion = "v1",
                CreatedAt = Now,
                LastAccessedAt = Now,
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(SettingsWithAiVerdictCache(ttl, rowCeiling: 10));
            Assert.Equal(0, result.AiVerdictCacheRowsPruned);
        }
    }
}
