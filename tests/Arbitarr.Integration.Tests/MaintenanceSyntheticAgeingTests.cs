using Arbitarr.Core.Settings;
using Arbitarr.Data;
using Arbitarr.Data.Backup;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Maintenance;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-3a (AC22, synthetic ageing): drives <see cref="MaintenanceJob"/> through repeated
/// insert-then-prune cycles under an advancing <see cref="FakeTimeProvider"/> against a real SQLite
/// file opened through <see cref="SqliteConnectionFactory"/> (so the connection actually gets the
/// M7-3a <c>auto_vacuum = INCREMENTAL</c> pragma applied at file-creation time, not just the raw
/// <c>UseSqlite(...)</c> wiring used by the unit-level <c>MaintenanceJobTests</c>), and asserts:
///   1. row counts and on-disk file size stabilise rather than growing without bound;
///   2. pruning actually reclaims pages (PRAGMA page_count/freelist_count), so a VACUUM-less
///      delete cycle that only grows the file would fail this test;
///   3. no audit-log entry inside its retention window is ever evicted.
///
/// Both the suppression audit log (retention-window pruning) and the search-result cache
/// (strict <c>age &gt; serve_until</c> pruning, M3) are driven through the same insert/prune/vacuum
/// cycle, so the steady-state and page-reclaim assertions cover the two tables that accumulate
/// fastest in production.
/// </summary>
public sealed class MaintenanceSyntheticAgeingTests : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly FakeTimeProvider _timeProvider;
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public MaintenanceSyntheticAgeingTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"arbitarr-m7-3a-ageing-{Guid.NewGuid():N}.db");
        _connectionFactory = new SqliteConnectionFactory(new SqliteConnectionOptions { DatabasePath = _databasePath });
        // arb-itmm: mirrors the composition root — the one-time WAL + auto_vacuum conversion runs
        // once, on a fresh file, before anything opens it, because OpenConnection now only VERIFIES
        // the journal mode. This is also what keeps point 2 above true: auto_vacuum only takes
        // effect on the connection that CREATES the file, which is this call's connection.
        _connectionFactory.ConvertToWalOnce();
        _timeProvider = new FakeTimeProvider(Start);

        using var context = CreateContext();
        context.Database.Migrate();
    }

    /// <summary>
    /// Deletes this class's database AND ITS SIDECARS, and FAILS if it cannot (arb-yt7j).
    ///
    /// <para>There is no host and no config directory here — this class owns a bare SQLite file
    /// under the temp directory — so <c>ConfigDirectoryTeardown</c>, which takes a directory, does
    /// not apply, and there is no background work to drain. What this shares with the classes that
    /// do have a host is the two defects that made the old cleanup silently useless.</para>
    ///
    /// <para><b>It deleted only the main file.</b> The connection runs in WAL mode
    /// (<c>ConvertToWalOnce</c> in the constructor, load-bearing for this class's page-reclaim
    /// assertion), so SQLite keeps <c>-wal</c> and <c>-shm</c> sidecars beside it. Deleting one of
    /// the three left the other two: measured, this class run alone leaked 3 files per run before
    /// this change and leaks none after.</para>
    ///
    /// <para><b>And it swallowed the failure</b> in an empty <c>catch (IOException)</c> — the shape
    /// CLAUDE.md §4 names, where the cleanup stops working and every run stays green regardless. A
    /// failed delete now throws, which is right for a class deleting its OWN file because the
    /// failure lands on the class responsible for it. <c>UnauthorizedAccessException</c> is caught
    /// alongside <c>IOException</c> for the reason <see cref="TestSupport.ConfigDirectoryTeardown"/>
    /// gives: Windows raises that one for a file another handle still holds open, and catching only
    /// <c>IOException</c> let it escape looking like an unrelated failure.</para>
    /// </summary>
    public void Dispose()
    {
        // Scoped to this class's own file rather than ClearAllPools(), which would also close
        // pooled connections belonging to test classes running in parallel (arb-rga.3).
        //
        // SqlitePoolCleaner.ClearPoolsFor, NOT SqlitePools.ClearPoolForFile, and that swap is what
        // makes the delete below possible at all (arb-yt7j). Pools are keyed by the FULL connection
        // string. ClearPoolForFile builds a bare "Data Source=<path>" string, but nothing in this
        // class ever opens one: every connection here comes from SqliteConnectionFactory, whose
        // string carries the pragmas DatabaseConnectionStrings spells out. So the old clear named a
        // pool that was always empty and closed nothing, while the real handles stayed checked out —
        // the delete then failed, and the empty catch(IOException) is why it failed in silence.
        // This class opens only the Application string (SqliteConnectionOptions.ToConnectionString
        // delegates to DatabaseConnectionStrings.Application). ClearPoolsFor also clears Maintenance,
        // which is harmless surplus — nothing here ever opens that pool either. This is the same
        // inventory-vs-ownership trap ConfigDirectoryTeardown documents; here it really was the
        // inventory.
        SqlitePoolCleaner.ClearPoolsFor(_databasePath);

        // The sidecars are part of the database rather than incidental litter: in WAL mode SQLite
        // writes pages into -wal and coordinates readers through -shm, so removing only the main
        // file leaves two behind on every run.
        var failures = new List<Exception>();

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Test cleanup could not delete its SQLite database or a WAL sidecar beside it " +
                $"({_databasePath}). The pool clear above releases this class's own handles, so a " +
                "failure here means a handle outlived them — check that every ArbitarrDbContext and " +
                "every OpenConnection in this class is disposed before teardown runs.",
                failures);
        }
    }

    private ArbitarrDbContext CreateContext()
        => new(ArbitarrDbContextOptionsFactory.Create(_connectionFactory));

    private static SettingsSnapshot SettingsWithRetention(TimeSpan retention)
        => SettingsSnapshot.Defaults(TimeSpan.FromMinutes(15)) with { SuppressionAuditRetention = retention, ServeUntil = retention };

    /// <summary>
    /// Size of the main database file after forcing a WAL checkpoint. In WAL mode, unflushed pages
    /// live in the <c>-wal</c> sidecar, so sampling the main file alone mid-run under-reports (it can
    /// sit at a single page for the whole run and then jump on the final checkpoint, which reads as
    /// false growth). Checkpoint first so every sample measures the same thing.
    /// </summary>
    private long MeasureCheckpointedFileSize()
    {
        QueryScalarLong("PRAGMA wal_checkpoint(TRUNCATE);");

        // The PRAGMA above is what makes the FileInfo length below meaningful (see the checkpoint
        // note on this method's summary). This ClearPoolForFile call is inert here: it names a bare
        // "Data Source=<path>" pool, but every connection in this class comes from
        // SqliteConnectionFactory, whose connection string carries additional pragmas — so, per the
        // Dispose comment above, it always clears an empty pool and never touches this class's real
        // handles. Removing it is deferred (tracked as a follow-up to arb-yt7j), left in place here
        // to keep this fix-up comments-only.
        SqlitePools.ClearPoolForFile(_databasePath);
        return new FileInfo(_databasePath).Length;
    }

    private long QueryScalarLong(string pragmaSql)
    {
        using var connection = _connectionFactory.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = pragmaSql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Simulates one "day" of production traffic: inserts a batch of audit-log rows timestamped at
    /// the current fake-clock time (so their age only accrues as the clock advances across cycles),
    /// then runs one maintenance pass.
    /// </summary>
    private async Task<MaintenanceJobResult> RunOneAgeingCycleAsync(int rowsToInsertThisCycle)
    {
        using (var context = CreateContext())
        {
            var now = _timeProvider.GetUtcNow();
            for (var i = 0; i < rowsToInsertThisCycle; i++)
            {
                context.SuppressionAuditLogEntries.Add(new SuppressionAuditLogEntry
                {
                    OccurredAt = now,
                    ReleaseIdentifier = $"release-{now.Ticks}-{i}",
                    QueryKey = "synthetic-ageing-query",
                    RuleName = "synthetic-rule",
                    Reason = "synthetic ageing test row",
                    ShadowMode = false,
                });
            }

            for (var i = 0; i < rowsToInsertThisCycle; i++)
            {
                context.SearchResultCacheEntries.Add(new SearchResultCacheEntry
                {
                    QueryKey = $"synthetic-ageing-query-{now.Ticks}-{i}",
                    PayloadJson = "[]",
                    FetchedAt = now,
                    FreshUntil = now + TimeSpan.FromMinutes(15),
                    ServeUntil = now + Retention,
                    LastRequestedAt = now,
                });
            }

            await context.SaveChangesAsync();
        }

        using var maintenanceContext = CreateContext();
        var job = new MaintenanceJob(maintenanceContext, _timeProvider);
        return await job.RunAsync(SettingsWithRetention(Retention));
    }

    [Fact]
    public async Task Repeated_ageing_cycles_stabilise_row_count_and_file_size_and_reclaim_pages()
    {
        const int rowsPerCycle = 200;
        const int cycles = 8;

        var rowCountsAfterEachCycle = new List<int>();
        var cacheRowCountsAfterEachCycle = new List<int>();
        var fileSizesAfterEachCycle = new List<long>();

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            await RunOneAgeingCycleAsync(rowsPerCycle);

            // Advance the clock by more than the retention window each cycle, so every row
            // inserted in a prior cycle becomes prunable on the *next* cycle's maintenance pass —
            // this is what "synthetic ageing" means here: simulated time, not wall-clock waiting.
            _timeProvider.Advance(Retention + TimeSpan.FromDays(1));

            using (var context = CreateContext())
            {
                rowCountsAfterEachCycle.Add(await context.SuppressionAuditLogEntries.CountAsync());
                cacheRowCountsAfterEachCycle.Add(await context.SearchResultCacheEntries.CountAsync());
            }

            fileSizesAfterEachCycle.Add(MeasureCheckpointedFileSize());
        }

        // One more cycle to prune everything aged past retention from the final insert batch too,
        // and to give the last insert's rows one full maintenance pass before we assert steady state.
        await RunOneAgeingCycleAsync(rowsPerCycle);
        _timeProvider.Advance(Retention + TimeSpan.FromDays(1));
        await RunOneAgeingCycleAsync(0);

        using (var context = CreateContext())
        {
            // (1) Row count stabilises: every row from every prior cycle is now well past its
            // 30-day retention window (the clock advanced 31+ days after each cycle), so the table
            // must not have grown without bound — it converges to (at most) the rows inserted in
            // the single most recent cycle rather than accumulating rowsPerCycle * cycles rows.
            var finalRowCount = await context.SuppressionAuditLogEntries.CountAsync();
            Assert.True(finalRowCount < rowsPerCycle,
                $"Expected row count to stabilise below one cycle's insert batch ({rowsPerCycle}), but was {finalRowCount} — rows are accumulating instead of being pruned.");

            // Same steady-state bound for the search-result cache: its prune predicate is strictly
            // age > serve_until (pinned to Retention above), so every prior cycle's rows are past it.
            var finalCacheRowCount = await context.SearchResultCacheEntries.CountAsync();
            Assert.True(finalCacheRowCount < rowsPerCycle,
                $"Expected search-result cache row count to stabilise below one cycle's insert batch ({rowsPerCycle}), but was {finalCacheRowCount} — SearchResultCacheEntries are accumulating instead of being pruned.");
            Assert.True(cacheRowCountsAfterEachCycle.Max() <= 2 * rowsPerCycle,
                $"Search-result cache peaked at {cacheRowCountsAfterEachCycle.Max()} rows mid-run; it must never hold more than two cycles' worth.");
        }

        // (2) File size stabilises rather than growing monotonically across cycles: compare the
        // last recorded size against the largest size seen mid-run. A VACUUM-less delete cycle
        // (rows logically removed but pages never reclaimed) would keep growing the file every
        // cycle, so the final size would exceed the max of all prior cycles instead of levelling.
        var finalFileSize = MeasureCheckpointedFileSize();
        var maxObservedFileSize = fileSizesAfterEachCycle.Max();
        Assert.True(finalFileSize <= maxObservedFileSize,
            $"Expected on-disk file size to stabilise/shrink after pruning (final {finalFileSize} bytes, max observed {maxObservedFileSize} bytes) — the file is still growing, which means pruning is not reclaiming space.");

        // (3) Pruning reclaims pages: after all this churn, PRAGMA freelist_count must be small
        // relative to page_count (i.e. the incremental_vacuum pragma is actually taking effect —
        // without auto_vacuum = INCREMENTAL having been set at file-creation time, freed pages
        // would sit in the freelist indefinitely instead of being returned to the OS, and this
        // ratio would stay elevated).
        var pageCount = QueryScalarLong("PRAGMA page_count;");
        var freelistCount = QueryScalarLong("PRAGMA freelist_count;");
        Assert.True(freelistCount <= pageCount / 4,
            $"Expected incremental_vacuum to keep the freelist small relative to total pages (page_count={pageCount}, freelist_count={freelistCount}) — a large freelist means pruned pages are not being reclaimed, i.e. incremental_vacuum is a no-op (likely because auto_vacuum was never set to INCREMENTAL at file-creation time).");
    }

    [Fact]
    public async Task No_audit_log_entry_inside_its_retention_window_is_evicted()
    {
        var retention = TimeSpan.FromDays(30);

        using (var context = CreateContext())
        {
            context.SuppressionAuditLogEntries.Add(new SuppressionAuditLogEntry
            {
                OccurredAt = _timeProvider.GetUtcNow(),
                ReleaseIdentifier = "still-within-retention",
                QueryKey = "synthetic-ageing-query",
                RuleName = "synthetic-rule",
                Reason = "must survive several maintenance passes",
                ShadowMode = false,
            });
            await context.SaveChangesAsync();
        }

        // Run several maintenance passes while advancing the clock, but never past the retention
        // window — the row must survive every single pass, not just the first one.
        for (var i = 0; i < 5; i++)
        {
            _timeProvider.Advance(retention / 10);

            using var context = CreateContext();
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(SettingsWithRetention(retention));

            Assert.Equal(0, result.SuppressionAuditLogRowsPruned);
        }

        using (var context = CreateContext())
        {
            Assert.Single(context.SuppressionAuditLogEntries, e => e.ReleaseIdentifier == "still-within-retention");
        }
    }

    /// <summary>
    /// The search-result cache counterpart of the audit-log survival check: a row younger than
    /// serve_until is legitimately valid fallback data (P1) and must survive every maintenance
    /// pass — pruning it early is the D3 anti-pattern <see cref="PrunePredicates"/> guards against.
    /// </summary>
    [Fact]
    public async Task No_search_result_cache_entry_inside_serve_until_is_evicted()
    {
        var serveUntil = TimeSpan.FromDays(30);

        using (var context = CreateContext())
        {
            var now = _timeProvider.GetUtcNow();
            context.SearchResultCacheEntries.Add(new SearchResultCacheEntry
            {
                QueryKey = "still-within-serve-until",
                PayloadJson = "[]",
                FetchedAt = now,
                FreshUntil = now + TimeSpan.FromMinutes(15),
                ServeUntil = now + serveUntil,
                LastRequestedAt = now,
            });
            await context.SaveChangesAsync();
        }

        for (var i = 0; i < 5; i++)
        {
            _timeProvider.Advance(serveUntil / 10);

            using var context = CreateContext();
            var job = new MaintenanceJob(context, _timeProvider);
            var result = await job.RunAsync(SettingsWithRetention(serveUntil));

            Assert.Equal(0, result.SearchResultCacheRowsPruned);
        }

        using (var context = CreateContext())
        {
            Assert.Single(context.SearchResultCacheEntries, e => e.QueryKey == "still-within-serve-until");
        }

        // One step past serve_until and the same row is pruned: the predicate is strict age > serve_until.
        _timeProvider.Advance(serveUntil);
        using (var context = CreateContext())
        {
            var result = await new MaintenanceJob(context, _timeProvider).RunAsync(SettingsWithRetention(serveUntil));
            Assert.Equal(1, result.SearchResultCacheRowsPruned);
        }
    }
}
