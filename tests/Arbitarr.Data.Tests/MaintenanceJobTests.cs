using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Arbitarr.Data.Maintenance;
using Arbitarr.TestSupport;
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
    private readonly SqliteTestDatabase _database = new("arr-searcher-maintenance-test");
    private readonly FakeTimeProvider _timeProvider;
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public MaintenanceJobTests()
    {
        _timeProvider = new FakeTimeProvider(Now);
    }

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    /// <summary>Defaults with a chosen session idle window, for the #44 prune tests.</summary>
    private static SettingsSnapshot SettingsWithIdleTimeout(TimeSpan idleTimeout) =>
        Settings(TimeSpan.FromDays(7)) with { SessionIdleTimeout = idleTimeout };

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

    // ---- Event store (#55) -------------------------------------------------------------------
    //
    // The event store is the fifth accumulating table. It shipped with retention written and tested
    // but NO CALLER (#70 wrote nothing to the table, so the gap was invisible); #55 turned on the
    // writes. These tests assert the scheduled job actually prunes it, which is the thing whose
    // absence would have made EventRetentionPolicy decorative and grown the SQLite file without
    // bound.

    /// <summary>
    /// #44: the session prune, asserted PER ROW.
    ///
    /// <para>Three rows are planted — one past its absolute expiry, one idle far beyond the
    /// configured window, and one live — and the survivors are checked BY IDENTITY, not by count.
    /// A count alone would pass against a prune that deleted the live row and kept a dead one,
    /// which is the failure that would sign the operator out on every maintenance pass. The live
    /// row is the positive control: without it, "the table shrank" would be satisfied by a prune
    /// that simply emptied the table.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_PrunesExpiredAndIdleSessionRows_ButKeepsTheLiveOne()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.Users.Add(new UserEntry
            {
                Id = 1,
                Username = "operator",
                PasswordHash = "not-a-real-hash",
                CreatedAt = Now,
            });

            // Past its absolute expiry: dead under every possible setting.
            context.Sessions.Add(new SessionEntry
            {
                UserId = 1,
                TokenHash = "absolutely-expired",
                CreatedAt = Now - TimeSpan.FromDays(40),
                LastSeenAt = Now - TimeSpan.FromDays(40),
                AbsoluteExpiresAt = Now - TimeSpan.FromSeconds(1),
            });

            // Idle far past the window (defaults are days, so a year is unambiguous), but still
            // inside its absolute expiry — so only the idle arm can remove it.
            context.Sessions.Add(new SessionEntry
            {
                UserId = 1,
                TokenHash = "long-idle",
                CreatedAt = Now - TimeSpan.FromDays(365),
                LastSeenAt = Now - TimeSpan.FromDays(365),
                AbsoluteExpiresAt = Now + TimeSpan.FromDays(30),
            });

            // Live and recently seen.
            context.Sessions.Add(new SessionEntry
            {
                UserId = 1,
                TokenHash = "live",
                CreatedAt = Now,
                LastSeenAt = Now,
                AbsoluteExpiresAt = Now + TimeSpan.FromDays(30),
            });

            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));

            Assert.Equal(2, result.ExpiredSessionRowsPruned);

            // PER ROW: the exact survivor, and the exact casualties.
            var remaining = context.Sessions.Select(s => s.TokenHash).ToList();
            Assert.Equal(new[] { "live" }, remaining);
        }
    }

    /// <summary>
    /// #44: a session idle beyond the CONFIGURED window but not beyond the delete margin is kept.
    ///
    /// <para>This is the settings-drift guard, and it is the reason the idle arm deletes on a
    /// multiple of the window rather than on the window itself. Such a row does not authenticate —
    /// <c>FindLiveByPresentedTokenAsync</c> refuses it against the exact window — but deleting it
    /// here would destroy a session that lengthening <c>session_idle_timeout</c> would have
    /// revived, signing the operator out because of a maintenance pass rather than their own
    /// change. Refusing to authenticate and deleting the row are deliberately different
    /// boundaries.</para>
    /// </summary>
    [Fact]
    public async Task RunAsync_KeepsSessionRow_IdlePastTheWindowButInsideTheDeleteMargin()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.Users.Add(new UserEntry
            {
                Id = 1,
                Username = "operator",
                PasswordHash = "not-a-real-hash",
                CreatedAt = Now,
            });

            // Settings below use a 7-day idle window; 10 days is past it, well inside the margin.
            context.Sessions.Add(new SessionEntry
            {
                UserId = 1,
                TokenHash = "idle-but-recoverable",
                CreatedAt = Now - TimeSpan.FromDays(10),
                LastSeenAt = Now - TimeSpan.FromDays(10),
                AbsoluteExpiresAt = Now + TimeSpan.FromDays(30),
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(SettingsWithIdleTimeout(TimeSpan.FromDays(7)));

            Assert.Equal(0, result.ExpiredSessionRowsPruned);
            Assert.Equal("idle-but-recoverable", Assert.Single(context.Sessions.ToList()).TokenHash);
        }
    }

    [Fact]
    public async Task RunAsync_PrunesOperationalEventRow_PastOperationalRetention()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.Events.Add(new EventEntry
            {
                Kind = EventKind.WorkerCycle,
                OccurredAt = Now - EventRetentionPolicy.OperationalRetention - TimeSpan.FromSeconds(1),
                Summary = "Worker cycle, one second past its window",
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));
            Assert.Equal(1, result.EventRowsPruned);
        }
    }

    /// <summary>
    /// arb-tps: the release lookup prune deletes the expired row and keeps the live one, in ONE
    /// pass over a table holding both. Asserted PER ROW rather than by count alone — "one row was
    /// deleted" still passes when the implementation deleted the wrong one, which for this table
    /// means breaking a download link that was still valid.
    /// </summary>
    [Fact]
    public async Task RunAsync_PrunesExpiredReleaseLookupRow_AndKeepsTheLiveOne()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.ReleaseLookupEntries.Add(new ReleaseLookupEntry
            {
                ProxyGuid = "expired-one-second-ago",
                SourceName = "hydra",
                PayloadJson = "{}",
                RecordedAt = Now - TimeSpan.FromDays(14),
                ExpiresAt = Now - TimeSpan.FromSeconds(1),
            });
            context.ReleaseLookupEntries.Add(new ReleaseLookupEntry
            {
                ProxyGuid = "live-one-second-left",
                SourceName = "hydra",
                PayloadJson = "{}",
                RecordedAt = Now - TimeSpan.FromDays(14),
                ExpiresAt = Now + TimeSpan.FromSeconds(1),
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));

            Assert.Equal(1, result.ReleaseLookupRowsPruned);

            // PER ROW: the exact survivor, and the exact casualty.
            var remaining = context.ReleaseLookupEntries.Select(e => e.ProxyGuid).ToList();
            Assert.Equal(new[] { "live-one-second-left" }, remaining);
        }
    }

    /// <summary>
    /// arb-tps: a row exactly AT its expiry is pruned, matching
    /// <c>PrunePredicates.IsReleaseLookupEntryPrunable</c>'s inclusive boundary and
    /// <c>ReleaseLookupStore.FindAsync</c>, which stops resolving it at that same instant. A row
    /// that no longer resolves but is never deleted would accumulate forever.
    /// </summary>
    [Fact]
    public async Task RunAsync_PrunesReleaseLookupRow_ExactlyAtExpiry()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.ReleaseLookupEntries.Add(new ReleaseLookupEntry
            {
                ProxyGuid = "expiring-exactly-now",
                SourceName = "hydra",
                PayloadJson = "{}",
                RecordedAt = Now - TimeSpan.FromDays(14),
                ExpiresAt = Now,
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));

            Assert.Equal(1, result.ReleaseLookupRowsPruned);
            Assert.Empty(context.ReleaseLookupEntries.ToList());
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotPruneOperationalEventRow_WithinOperationalRetention()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.Events.Add(new EventEntry
            {
                Kind = EventKind.WorkerCycle,
                OccurredAt = Now - EventRetentionPolicy.OperationalRetention + TimeSpan.FromSeconds(1),
                Summary = "Worker cycle, one second inside its window",
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));
            Assert.Equal(0, result.EventRowsPruned);
        }
    }

    /// <summary>
    /// The asymmetry survives the scheduled job, not just a direct PruneAsync call: a decision older
    /// than the 7-day operational window must still be here, because decisions are kept for 180 days
    /// so #54's agreement rate has a sample to compute over. A job that pruned every kind on one
    /// clock would pass the two tests above and silently destroy that.
    /// </summary>
    [Fact]
    public async Task RunAsync_KeepsDecisionRowsOlderThanTheOperationalWindow()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.Events.Add(new EventEntry
            {
                Kind = EventKind.Decision,
                OccurredAt = Now - EventRetentionPolicy.OperationalRetention - TimeSpan.FromDays(30),
                Summary = "Decision far past the operational window, far inside the decision one",
            });
            context.Events.Add(new EventEntry
            {
                Kind = EventKind.SearchServed,
                OccurredAt = Now - EventRetentionPolicy.OperationalRetention - TimeSpan.FromDays(30),
                Summary = "Operational event of exactly the same age",
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));

            // Only the operational row goes, despite both being identically aged.
            Assert.Equal(1, result.EventRowsPruned);
        }

        using (var context = CreateContext())
        {
            var survivor = Assert.Single(await context.Events.ToListAsync());
            Assert.Equal(EventKind.Decision, survivor.Kind);
        }
    }

    [Fact]
    public async Task RunAsync_PrunesDecisionRow_PastDecisionRetention()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.Events.Add(new EventEntry
            {
                Kind = EventKind.Decision,
                OccurredAt = Now - EventRetentionPolicy.DecisionRetention - TimeSpan.FromSeconds(1),
                Summary = "Decision one second past even the 180-day window",
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(Settings(TimeSpan.FromDays(7)));

            // Long retention is not unbounded retention.
            Assert.Equal(1, result.EventRowsPruned);
        }
    }
}
