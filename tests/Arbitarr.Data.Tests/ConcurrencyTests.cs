using System.Diagnostics;
using System.Linq;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Caching;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Data.Tests;

/// <summary>
/// Proves AC15a: with WAL journal mode and an explicit busy_timeout, a background writer
/// continuously inserting/updating rows (simulating the classifier) never causes a concurrent
/// foreground reader to stall, time out, or throw a SQLITE_BUSY-style exception during a
/// sustained real-concurrency run.
///
/// <see cref="ConcurrentWriteWhileRead_UnderMisconfiguredJournalMode_ReaderFailsWithBusy"/> proves
/// the harness is non-vacuous: run against DELETE journal mode with a near-zero busy_timeout, the
/// exact same contention pattern reliably produces SQLITE_BUSY failures. Only the WAL+busy_timeout
/// test (<see cref="ConcurrentWriteWhileRead_WithWalAndBusyTimeout_ReaderNeverStalls"/>) is
/// expected to pass, which is the fail-then-fix-then-pass evidence this file exists to record.
///
/// Investigation note (recorded because it materially shaped the harness design): a first version
/// of this harness used ADO.NET's default deferred <c>BeginTransaction()</c> for the writer, which
/// only escalates to SQLite's RESERVED/EXCLUSIVE lock lazily and briefly, so a concurrent plain
/// SELECT almost never actually observed contention regardless of journal mode or busy_timeout —
/// producing a false pass on the misconfigured-mode proof (vacuous, not a real repro). Standalone
/// repro against raw Microsoft.Data.Sqlite confirmed: (1) a plain reader does not conflict with a
/// writer's RESERVED lock in SQLite's locking model at all — only writer-vs-writer contention (or a
/// reader caught in the narrow COMMIT-time EXCLUSIVE-upgrade window) produces SQLITE_BUSY; and
/// (2) the writer must issue <c>BEGIN IMMEDIATE</c> to take the RESERVED lock immediately at
/// transaction start (deferred BEGIN only takes it lazily on first write, and even then briefly).
/// The harness below reflects both findings: writers use <c>BEGIN IMMEDIATE</c> so a second
/// concurrent writer reliably contends, and each command carries a short, explicit
/// <see cref="SqliteCommand.CommandTimeout"/> so a genuine SQLITE_BUSY surfaces as a fast exception
/// instead of blocking the test for the full busy_timeout/command-timeout ceiling.
/// </summary>
public sealed class ConcurrencyTests : IDisposable
{
    private readonly string _dbPath;

    public ConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-concurrency-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ConcurrentWriteWhileRead_WithWalAndBusyTimeout_ReaderNeverStalls()
    {
        var factory = new SqliteConnectionFactory(new SqliteConnectionOptions
        {
            DatabasePath = _dbPath,
            BusyTimeoutMilliseconds = SqliteConnectionOptions.DefaultBusyTimeoutMilliseconds,
        });

        RunConcurrentContention(
            openWriterConnection: () => factory.OpenConnection(),
            openReaderConnection: () => factory.OpenConnection(),
            adoCommandTimeoutSeconds: 10,
            expectFailures: false);
    }

    /// <summary>
    /// Non-vacuousness proof (mirrors Step 1's AC6a treatment): the same contention pattern,
    /// against a deliberately misconfigured connection (default rollback-journal mode, a
    /// near-zero busy_timeout, and a short ADO command timeout so a real SQLITE_BUSY surfaces
    /// quickly instead of blocking), reliably produces failures. This demonstrates the test
    /// harness actually contends and would catch a regression, rather than passing vacuously
    /// regardless of configuration.
    /// </summary>
    [Fact]
    public void ConcurrentWriteWhileRead_UnderMisconfiguredJournalMode_ReaderFailsWithBusy()
    {
        SqliteConnection OpenMisconfiguredConnection()
        {
            var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var journalModeCommand = connection.CreateCommand();
            // Deliberately the opposite of AC15a's requirement: default rollback journal mode
            // (which takes a RESERVED/EXCLUSIVE lock for the duration of a write transaction)
            // with busy_timeout left at effectively zero (SQLite's own no-retry default).
            journalModeCommand.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA busy_timeout = 1;";
            journalModeCommand.ExecuteNonQuery();
            return connection;
        }

        RunConcurrentContention(
            openWriterConnection: OpenMisconfiguredConnection,
            openReaderConnection: OpenMisconfiguredConnection,
            adoCommandTimeoutSeconds: 1,
            expectFailures: true);
    }

    private static void RunConcurrentContention(
        Func<SqliteConnection> openWriterConnection,
        Func<SqliteConnection> openReaderConnection,
        int adoCommandTimeoutSeconds,
        bool expectFailures)
    {
        const int durationMilliseconds = 4000;
        const int writerCount = 3;
        const int readerCount = 4;

        using (var setupConnection = openWriterConnection())
        using (var createCommand = setupConnection.CreateCommand())
        {
            createCommand.CommandText =
                "CREATE TABLE IF NOT EXISTS concurrency_probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL);";
            createCommand.ExecuteNonQuery();
        }

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        long readCount = 0;
        long writeCount = 0;

        // Deflake root cause (was: `No reader ever completed a single read — harness setup is
        // broken`, observed both locally and on CI under parallel test-runner load): the original
        // harness started the 4-second CancellationTokenSource *before* queuing the writer/reader
        // work to the thread pool via Task.Run. Under contention for thread-pool worker threads
        // (e.g. several `dotnet test` processes/projects running concurrently in CI), a queued task
        // can sit unscheduled for a long tail of milliseconds — occasionally consuming the entire
        // window before a reader task ever gets a thread to run its first iteration on. That is a
        // scheduler-latency artifact, not a SQLite locking failure, so widening the 4s duration
        // would only mask it (still flaky at heavier CI load) rather than fix it.
        //
        // Fix: (1) run each worker on a dedicated, non-pooled thread via
        // TaskCreationOptions.LongRunning, which requests its own OS thread instead of a
        // potentially-starved thread-pool slot; and (2) use a Barrier — with this test method
        // itself as one of the participants — so the timed contention window only starts (the
        // CancellationTokenSource is only constructed) once every writer/reader thread has
        // actually started running (past its connection-open, the slowest per-thread startup
        // cost) and is waiting at the barrier. No thread's "first useful iteration" can be
        // silently swallowed by pre-window scheduler/startup latency.
        using var startBarrier = new Barrier(writerCount + readerCount + 1);
        var stopFlag = 0;
        bool ShouldStop() => Volatile.Read(ref stopFlag) != 0;

        // Multiple concurrent writers: this is the realistic AC15a shape (the background
        // classifier's continuous writes contending with each other and with reads), and it is
        // also what makes lock contention reliably observable — see the class-level note on why
        // a single lone writer against a passive reader rarely contends at all.
        var writerTasks = Enumerable.Range(0, writerCount).Select(writerIndex => Task.Factory.StartNew(() =>
        {
            using var writerConnection = openWriterConnection();
            var row = writerIndex * 1000;
            startBarrier.SignalAndWait();
            while (!ShouldStop())
            {
                try
                {
                    using (var beginCommand = writerConnection.CreateCommand())
                    {
                        beginCommand.CommandTimeout = adoCommandTimeoutSeconds;
                        // BEGIN IMMEDIATE takes the RESERVED lock up front, unlike a deferred
                        // transaction which only escalates lazily and briefly on first write.
                        beginCommand.CommandText = "BEGIN IMMEDIATE;";
                        beginCommand.ExecuteNonQuery();
                    }

                    using (var insertCommand = writerConnection.CreateCommand())
                    {
                        insertCommand.CommandTimeout = adoCommandTimeoutSeconds;
                        insertCommand.CommandText =
                            "INSERT INTO concurrency_probe (id, value) VALUES ($id, $value) " +
                            "ON CONFLICT(id) DO UPDATE SET value = excluded.value;";
                        insertCommand.Parameters.AddWithValue("$id", row % 50);
                        insertCommand.Parameters.AddWithValue("$value", Guid.NewGuid().ToString());
                        insertCommand.ExecuteNonQuery();
                    }

                    // Hold the write lock open briefly so it genuinely overlaps with other
                    // writers'/readers' concurrent attempts, rather than the transaction
                    // beginning and committing so fast that overlap is left to chance.
                    Thread.Sleep(5);

                    using (var commitCommand = writerConnection.CreateCommand())
                    {
                        commitCommand.CommandTimeout = adoCommandTimeoutSeconds;
                        commitCommand.CommandText = "COMMIT;";
                        commitCommand.ExecuteNonQuery();
                    }

                    Interlocked.Increment(ref writeCount);
                    row++;
                }
                catch (Exception e)
                {
                    failures.Add(e);
                    TryRollback(writerConnection);
                }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        var readerTasks = Enumerable.Range(0, readerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            using var readerConnection = openReaderConnection();
            startBarrier.SignalAndWait();
            while (!ShouldStop())
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    using var selectCommand = readerConnection.CreateCommand();
                    selectCommand.CommandTimeout = adoCommandTimeoutSeconds;
                    selectCommand.CommandText = "SELECT COUNT(*) FROM concurrency_probe;";
                    selectCommand.ExecuteScalar();
                    Interlocked.Increment(ref readCount);

                    // A reader that "never blocks" per AC15a should complete a trivial COUNT(*)
                    // query well under a second even under sustained writer contention.
                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
                    {
                        failures.Add(new TimeoutException(
                            $"Reader query took {stopwatch.Elapsed.TotalMilliseconds:F0}ms, exceeding the 1s stall threshold."));
                    }
                }
                catch (Exception e)
                {
                    failures.Add(e);
                }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        // This test thread is the final barrier participant: SignalAndWait() here only returns
        // once every writer/reader thread has opened its connection and reached its own
        // SignalAndWait() call — i.e. the timed contention window below starts only once all
        // workers are actually running, eliminating pre-window scheduler/startup latency as a
        // source of "no reader/writer ever ran an iteration" flakiness.
        startBarrier.SignalAndWait();
        Thread.Sleep(durationMilliseconds);
        Volatile.Write(ref stopFlag, 1);

        Task.WaitAll(writerTasks.Concat(readerTasks).ToArray());

        Assert.True(writeCount > 0, "No writer ever completed a single write — harness setup is broken.");
        Assert.True(readCount > 0, "No reader ever completed a single read — harness setup is broken.");

        if (expectFailures)
        {
            Assert.True(
                failures.Count > 0,
                "Expected the misconfigured (DELETE journal, near-zero busy_timeout) run to " +
                "produce at least one SQLITE_BUSY-style failure under contention, proving the " +
                "harness actually contends; none occurred, which would make the WAL-mode test " +
                "vacuous.");
        }
        else if (!failures.IsEmpty)
        {
            throw new AggregateException(
                $"Encountered {failures.Count} unexpected failure(s)/stall(s) under WAL mode " +
                "with busy_timeout configured — AC15a violated.",
                failures);
        }
    }

    /// <summary>
    /// M3-7/AC15a refinement: the >1s stall threshold in
    /// <see cref="ConcurrentWriteWhileRead_WithWalAndBusyTimeout_ReaderNeverStalls"/> only catches
    /// gross stalls. This test proves the tighter, quantitative claim: reader latency under
    /// sustained writer contention must stay close to its own uncontended baseline, not merely
    /// "under some absolute ceiling."
    ///
    /// <para>
    /// M3-7 rework: the contended-phase writer is the real production writer,
    /// <see cref="RefreshWorker.RunCycleAsync"/> — driven directly in a tight loop (not via the
    /// <c>BackgroundService</c> host) at its most aggressive legal pacing (near-zero
    /// <c>WorkerCycleInterval</c>/<c>RepopulationSpreadWindow</c>, real wall-clock
    /// <see cref="TimeProvider.System"/>) — rather than generic <c>BEGIN IMMEDIATE</c> writers.
    /// Reads go through the real read path, <see cref="SearchResultCache.GetAsync"/>, over a
    /// pre-seeded dataset that stays continuously eligible for
    /// <see cref="ISearchResultCacheStore.GetRefreshCandidatesAsync"/> so the writer is kept
    /// genuinely busy for the whole contended phase. Both phases run back-to-back in this same
    /// test execution (same machine, same process, same moment) so the comparison is never
    /// confounded by run-to-run environment noise, and a warm-up phase (discarded) precedes the
    /// measured baseline so JIT/first-connection costs don't pollute it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReaderLatency_P95UnderContention_StaysWithinTwentyPercentOfUncontendedBaseline()
    {
        const int seededKeyCount = 25;
        const int readerCount = 4;

        // Touch the file once via the production connection factory so WAL mode is set and
        // verified before EF Core opens the same file.
        var factory = new SqliteConnectionFactory(new SqliteConnectionOptions
        {
            DatabasePath = _dbPath,
            BusyTimeoutMilliseconds = SqliteConnectionOptions.DefaultBusyTimeoutMilliseconds,
        });
        using (factory.OpenConnection())
        {
        }

        using (var migrationContext = CreateContext())
        {
            migrationContext.Database.Migrate();
        }

        var now = DateTimeOffset.UtcNow;
        var queryKeys = Enumerable.Range(0, seededKeyCount).Select(i => $"m3-7-contention-key-{i}").ToArray();

        // Seed rows that are "active" (recently requested) but already past FreshUntil - so
        // RefreshWorker.RunCycleAsync's GetRefreshCandidatesAsync selection predicate
        // (LastRequestedAt > now - activeWindow AND now >= FreshUntil - refreshLead) keeps
        // re-selecting every one of them each cycle, keeping the writer continuously busy.
        using (var seedContext = CreateContext())
        {
            var seedStore = new SearchResultCacheStore(seedContext);
            foreach (var key in queryKeys)
            {
                await seedStore.SaveAsync(
                    key,
                    payloadJson: "{\"results\":[]}",
                    fetchedAt: now - TimeSpan.FromMinutes(10),
                    freshUntil: now - TimeSpan.FromMinutes(5),
                    serveUntil: now + TimeSpan.FromHours(1));
                await seedStore.TouchLastRequestedAsync(key, now);
            }
        }

        // Warm-up phase (discarded): pays for JIT/first-connection costs so they don't pollute
        // the measured baseline.
        await RunReaderLatencyProbeAsync(queryKeys, TimeSpan.FromMilliseconds(500), readerCount, minSamples: 1);

        // Baseline phase: readers alone via the real SearchResultCache.GetAsync path, no writer
        // running.
        var baselineLatencies = await RunReaderLatencyProbeAsync(queryKeys, TimeSpan.FromSeconds(3), readerCount, minSamples: 200);

        // Contended phase: identical reader probe, now racing the real RefreshWorker driven at
        // its most aggressive legal pacing (RepopulationSpreadWindow must stay > 0 ticks per
        // RepopulationPacer.Plan, so use the smallest legal value rather than exactly Zero).
        var workerOptions = new RefreshWorkerOptions(
            Enabled: true,
            WorkerCycleInterval: TimeSpan.FromTicks(1),
            ActiveWindow: TimeSpan.FromHours(1),
            RefreshLead: TimeSpan.FromHours(1),
            FreshUntilAge: TimeSpan.FromMinutes(5),
            ServeUntilAge: TimeSpan.FromHours(1),
            RepopulationSpreadWindow: TimeSpan.FromTicks(1),
            MaxConcurrentRefreshes: 4);

        using var writerContext = CreateContext();
        var writerStore = new SearchResultCacheStore(writerContext);
        var writerCache = new SearchResultCache(writerStore, TimeProvider.System);
        var worker = new RefreshWorker(
            writerStore,
            writerCache,
            new AlwaysAllowCircuitBreaker(),
            fetcher: (_, _, _) => Task.FromResult<string?>("{\"results\":[]}"),
            TimeProvider.System,
            workerOptions,
            sourceName: "m3-7-contention-source");

        using var stopSignal = new CancellationTokenSource();
        var writerTask = Task.Run(async () =>
        {
            while (!stopSignal.IsCancellationRequested)
            {
                try
                {
                    await worker.RunCycleAsync(stopSignal.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected once the contended phase stops the writer.
                }
            }
        });

        List<double> contendedLatencies;
        try
        {
            contendedLatencies = await RunReaderLatencyProbeAsync(queryKeys, TimeSpan.FromSeconds(3), readerCount, minSamples: 200);
        }
        finally
        {
            stopSignal.Cancel();
            await writerTask;
        }

        var baselineP95 = Percentile(baselineLatencies, 0.95);
        var contendedP95 = Percentile(contendedLatencies, 0.95);
        var ceiling = baselineP95 * 1.20;

        Assert.True(
            contendedP95 <= ceiling,
            $"Contended p95 reader latency ({contendedP95:F2}ms) exceeded 120% of the same-run " +
            $"uncontended baseline p95 ({baselineP95:F2}ms, ceiling {ceiling:F2}ms) -- AC15a " +
            "requires WAL-mode reads to remain effectively unaffected by concurrent writers, not " +
            "merely avoid multi-second stalls.");
    }

    /// <summary>
    /// A circuit breaker fake that always permits calls and records nothing -- the breaker's own
    /// behavior is not under test here; only <see cref="RefreshWorker.RunCycleAsync"/>'s real
    /// store/cache/pacer interaction is.
    /// </summary>
    private sealed class AlwaysAllowCircuitBreaker : IAsyncCircuitBreaker
    {
        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// Runs <see cref="SearchResultCache.GetAsync"/> reads back-to-back across
    /// <paramref name="readerCount"/> concurrent tasks (each with its own
    /// <see cref="ArbitarrDbContext"/>/<see cref="SearchResultCacheStore"/>, since EF Core
    /// contexts are not thread-safe to share) for <paramref name="duration"/>, cycling through
    /// <paramref name="queryKeys"/>, returning every individual query's wall-clock latency in
    /// milliseconds.
    /// </summary>
    private async Task<List<double>> RunReaderLatencyProbeAsync(string[] queryKeys, TimeSpan duration, int readerCount, int minSamples)
    {
        using var stopSignal = new CancellationTokenSource(duration);
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();

        var readerTasks = Enumerable.Range(0, readerCount).Select(readerIndex => Task.Run(async () =>
        {
            using var readerContext = CreateContext();
            var readerCache = new SearchResultCache(new SearchResultCacheStore(readerContext), TimeProvider.System);
            var i = readerIndex;
            while (!stopSignal.IsCancellationRequested)
            {
                var key = queryKeys[i % queryKeys.Length];
                i++;
                var stopwatch = Stopwatch.StartNew();
                await readerCache.GetAsync(key, refreshTrigger: null);
                stopwatch.Stop();
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        })).ToArray();

        await Task.WhenAll(readerTasks);

        Assert.True(latencies.Count >= minSamples,
            $"Reader latency probe collected {latencies.Count} samples, fewer than the required {minSamples}.");
        return latencies.ToList();
    }

    private static double Percentile(List<double> samples, double percentile)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        index = Math.Clamp(index, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            using var rollbackCommand = connection.CreateCommand();
            rollbackCommand.CommandTimeout = 1;
            rollbackCommand.CommandText = "ROLLBACK;";
            rollbackCommand.ExecuteNonQuery();
        }
        catch
        {
            // Best-effort: if there was no open transaction to roll back (e.g. BEGIN itself
            // failed), this throws and is intentionally swallowed.
        }
    }
}
