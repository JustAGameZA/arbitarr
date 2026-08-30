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
/// Serialises this class against the rest of Arbitarr.Data.Tests. These are wall-clock timing
/// assertions: a sibling test class running on the same box during the measurement window steals
/// CPU from the reader threads and inflates the tail. Scoped deliberately to this one class via a
/// collection rather than an xunit.runner.json, so every other test in the project keeps running in
/// parallel and the project's execution semantics (and the test-count floor) are unchanged.
/// </summary>
[CollectionDefinition(ConcurrencyTests.SerialTimingCollection, DisableParallelization = true)]
public sealed class SerialTimingCollectionDefinition;

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
[Collection(ConcurrencyTests.SerialTimingCollection)]
public sealed class ConcurrencyTests : IDisposable
{
    internal const string SerialTimingCollection = "data-serial-timing";

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
    /// gross stalls, and it drives synthetic <c>BEGIN IMMEDIATE</c> writers against a scratch
    /// table. This test proves the same AC15a property against the *real* production write path --
    /// <see cref="RefreshWorker.RunCycleAsync"/> writing back real cache entries -- and asserts a
    /// tighter tail-latency bound plus a no-starvation property that the sibling has no analogue of.
    ///
    /// <para>
    /// Flake rework (#21) -- why this no longer compares against an uncontended baseline. The
    /// original form asserted "contended p95 &lt;= 120% of this run's own uncontended baseline p95".
    /// That assertion was unsound for two independent reasons, and no amount of estimator
    /// hardening (more trials, interleaving, median-of-p95) could rescue it:
    /// </para>
    ///
    /// <para>
    /// (1) The probe was not a reader. <see cref="SearchResultCache.GetAsync"/> stamps
    /// <c>LastRequestedAt</c> on every servable band, and
    /// <see cref="ISearchResultCacheStore.TouchLastRequestedAsync"/> issues a real UPDATE. Because
    /// the seeded rows below are deliberately stale-but-valid (so the writer keeps selecting
    /// them), every probe iteration performed a write. The "uncontended baseline" was therefore
    /// already N concurrent writers contending for SQLite's single global write lock -- precisely
    /// the thing WAL does not eliminate -- so the measurement never exercised AC15a's actual claim
    /// that readers do not block on writers. This test now probes
    /// <see cref="ISearchResultCacheStore.GetAsync"/> directly, which is genuinely read-only
    /// (AsNoTracking, no SaveChanges), and is the exact call
    /// <see cref="SearchResultCache.GetAsync"/> delegates its read to.
    /// </para>
    ///
    /// <para>
    /// (2) The ratio had no stable central tendency. Under <c>busy_timeout</c> a blocked writer
    /// sleeps on SQLite's discrete backoff schedule (1, 2, 5, 10, 15, 20, 25, 50, 100ms...), so the
    /// latency distribution is bimodal and the p95 index lands on whichever side of a backoff step
    /// the run happened to fall. Measured under CPU saturation, the contended/baseline ratio
    /// wandered across 0.29x-3.52x with 12 of 19 runs breaching the 1.20 ceiling; per-trial ratios
    /// within a single 5-trial run spanned 0.049x to 35.583x. Ratios well below 1.0 -- contended
    /// reads measuring *faster* than their own baseline -- confirm the quantity was dominated by
    /// scheduler and lock-queue artifacts rather than by any contention effect. See issue #21 for
    /// the full dataset.
    /// </para>
    ///
    /// <para>
    /// What is asserted instead: with a genuinely read-only probe the expected effect size is
    /// nil -- that is what "WAL works" means -- so the right shape is the absence of a cliff, not
    /// the smallness of a slope. Two properties are checked while the real writer runs flat out:
    /// an absolute tail-latency ceiling (a WAL reader takes no lock the writer holds, so blocking
    /// at all indicates the WAL machinery failed; the gap between a few ms and the
    /// <c>busy_timeout</c> backoff cliff is orders of magnitude, giving the ceiling enormous margin
    /// against scheduler jitter on a saturated 2-vCPU runner), and a throughput floor proving reads
    /// keep making progress rather than being starved behind the writer. Read counts over a fixed
    /// window concentrate far better than a ratio of two heavy-tailed p95s.
    /// </para>
    ///
    /// <para>
    /// Non-vacuousness is proved by
    /// <see cref="ReaderUnderRefreshWorkerContention_UnderMisconfiguredJournalMode_DegradesOrFails"/>,
    /// which runs this identical shape against a DELETE-journal database and shows these same
    /// assertions go red -- so a passing run here carries real information.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReaderUnderRefreshWorkerContention_StaysFastAndNeverStarves()
    {
        var outcome = await RunReaderUnderWriterContentionAsync(useWalMode: true);

        Assert.Empty(outcome.Failures);

        Assert.True(
            outcome.MaxLatencyMs <= MaxContendedReadLatencyMs,
            $"Slowest contended read took {outcome.MaxLatencyMs:F1}ms, exceeding the " +
            $"{MaxContendedReadLatencyMs}ms ceiling (p99 {outcome.P99LatencyMs:F1}ms, median " +
            $"{outcome.MedianLatencyMs:F2}ms over {outcome.ReadCount} reads against " +
            $"{outcome.WriteCycleCount} writer cycles) -- AC15a requires a WAL-mode read to take " +
            "no lock a concurrent writer holds, so any read blocking on this scale indicates the " +
            "WAL machinery is not in effect.");

        Assert.True(
            outcome.ReadCount >= MinContendedReads,
            $"Only {outcome.ReadCount} reads completed while the writer ran (expected at least " +
            $"{MinContendedReads}) -- readers are being starved behind the writer, which is the " +
            "AC15a violation this test exists to catch.");
    }

    /// <summary>
    /// Non-vacuousness proof for <see cref="ReaderUnderRefreshWorkerContention_StaysFastAndNeverStarves"/>
    /// (mirrors the <see cref="ConcurrentWriteWhileRead_UnderMisconfiguredJournalMode_ReaderFailsWithBusy"/>
    /// treatment): the identical reader-vs-RefreshWorker shape against a database forced into
    /// rollback-journal mode, where a writer's RESERVED/EXCLUSIVE lock genuinely does exclude
    /// readers. At least one of the two guarded properties -- the tail-latency ceiling or the
    /// throughput floor -- must break, or an outright SQLITE_BUSY must surface. Without this, a
    /// green run of the WAL test above would be indistinguishable from a tautology.
    /// </summary>
    [Fact]
    public async Task ReaderUnderRefreshWorkerContention_UnderMisconfiguredJournalMode_DegradesOrFails()
    {
        var outcome = await RunReaderUnderWriterContentionAsync(useWalMode: false);

        var stalled = outcome.MaxLatencyMs > MaxContendedReadLatencyMs;
        var starved = outcome.ReadCount < MinContendedReads;
        var errored = outcome.Failures.Count > 0;

        Assert.True(
            stalled || starved || errored,
            "Expected the rollback-journal run to violate at least one of the guarded properties " +
            $"(slowest read {outcome.MaxLatencyMs:F1}ms vs {MaxContendedReadLatencyMs}ms ceiling; " +
            $"{outcome.ReadCount} reads vs {MinContendedReads} floor; {outcome.Failures.Count} " +
            "errors), proving the WAL-mode assertions are not vacuous. None was violated, so " +
            "those assertions would pass regardless of journal mode and prove nothing.");
    }

    /// <summary>
    /// Absolute ceiling for any single contended read. A WAL-mode reader acquires no lock a writer
    /// holds, so a correct run sits in the sub-millisecond-to-few-milliseconds range; a reader that
    /// is actually excluded by a writer lands on SQLite's busy_timeout backoff schedule, orders of
    /// magnitude above. The ceiling sits in that gap: loose enough to absorb scheduler jitter and
    /// GC pauses on a saturated 2-vCPU CI runner, still ~4x tighter than the sibling test's 1s.
    /// </summary>
    private const double MaxContendedReadLatencyMs = 250.0;

    /// <summary>
    /// Throughput floor: the anti-starvation property. Deliberately far below what a healthy run
    /// achieves (thousands of reads in the window) so ordinary CPU steal by the writer can never
    /// trip it, while a reader genuinely serialized behind the writer -- an order-of-magnitude
    /// throughput collapse -- does.
    /// </summary>
    private const int MinContendedReads = 200;

    private sealed record ContentionOutcome(
        int ReadCount,
        int WriteCycleCount,
        double MedianLatencyMs,
        double P99LatencyMs,
        double MaxLatencyMs,
        IReadOnlyList<Exception> Failures);

    /// <summary>
    /// Runs the shared reader-vs-RefreshWorker contention shape used by both the WAL test and its
    /// non-vacuousness twin: seed rows that the worker will keep re-selecting, start the real
    /// <see cref="RefreshWorker"/> writing continuously, then measure genuinely read-only probes
    /// against it for a fixed window.
    /// </summary>
    private async Task<ContentionOutcome> RunReaderUnderWriterContentionAsync(bool useWalMode)
    {
        const int seededKeyCount = 25;
        // Match the runner rather than oversubscribing it: CI is a 2-vCPU box, and extra probe
        // threads on top of the writer's own concurrency only add run-queue wait to every measured
        // latency, which is machine scheduling noise rather than anything SQLite did.
        var readerCount = Math.Max(2, Environment.ProcessorCount / 2);
        var measureWindow = TimeSpan.FromSeconds(3);

        if (useWalMode)
        {
            // Touch the file once via the production connection factory so WAL mode is set and
            // verified before EF Core opens the same file.
            var factory = new SqliteConnectionFactory(new SqliteConnectionOptions
            {
                DatabasePath = _dbPath,
                BusyTimeoutMilliseconds = SqliteConnectionOptions.DefaultBusyTimeoutMilliseconds,
            });
            using var walConnection = factory.OpenConnection();
        }

        using (var migrationContext = CreateContext())
        {
            migrationContext.Database.Migrate();
        }

        if (!useWalMode)
        {
            // Deliberately the opposite of AC15a's requirement. journal_mode is persistent in the
            // database file, so this must run after Migrate() (which opens its own connection) to
            // actually take effect for the probes below.
            using var misconfigured = new SqliteConnection($"Data Source={_dbPath}");
            misconfigured.Open();
            using var pragma = misconfigured.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA busy_timeout = 1;";
            pragma.ExecuteNonQuery();
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

        // Pin the writer's cadence instead of letting it emit "as fast as this box can go": an
        // unbounded cycle rate makes the applied load a function of machine speed, so two runners
        // would be running two different experiments.
        var workerOptions = new RefreshWorkerOptions(
            Enabled: true,
            WorkerCycleInterval: TimeSpan.FromMilliseconds(10),
            ActiveWindow: TimeSpan.FromHours(1),
            RefreshLead: TimeSpan.FromHours(1),
            FreshUntilAge: TimeSpan.FromMinutes(5),
            ServeUntilAge: TimeSpan.FromHours(1),
            RepopulationSpreadWindow: TimeSpan.FromTicks(1),
            MaxConcurrentRefreshes: 2);

        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var writeCycleCount = 0;
        var stopFlag = 0;
        bool ShouldStop() => Volatile.Read(ref stopFlag) != 0;

        // LongRunning + Barrier for the same reason documented on RunConcurrentContention above:
        // pooled tasks can sit unscheduled long enough to swallow their whole measurement window
        // under parallel test-runner load, which would be a scheduler artifact rather than a
        // SQLite result.
        using var startBarrier = new Barrier(readerCount + 2);

        var writerThread = Task.Factory.StartNew(async () =>
        {
            using var writerContext = CreateContext();
            var writerStore = new SearchResultCacheStore(writerContext);
            var worker = new RefreshWorker(
                writerStore,
                new SearchResultCache(writerStore, TimeProvider.System),
                new AlwaysAllowCircuitBreaker(),
                fetcher: (_, _, _) => Task.FromResult<string?>("{\"results\":[]}"),
                TimeProvider.System,
                workerOptions,
                sourceName: "m3-7-contention-source");

            startBarrier.SignalAndWait();
            while (!ShouldStop())
            {
                try
                {
                    await worker.RunCycleAsync(CancellationToken.None);
                    Interlocked.Increment(ref writeCycleCount);
                }
                catch (Exception e)
                {
                    // The writer hitting SQLITE_BUSY is expected in the misconfigured twin and is
                    // not itself the property under test; only reader-side outcomes are asserted.
                    if (useWalMode)
                    {
                        failures.Add(e);
                    }
                }

                await Task.Delay(workerOptions.WorkerCycleInterval);
            }
        }, TaskCreationOptions.LongRunning).Unwrap();

        var readerThreads = Enumerable.Range(0, readerCount).Select(readerIndex => Task.Factory.StartNew(async () =>
        {
            using var readerContext = CreateContext();
            // Probe the store directly, NOT SearchResultCache.GetAsync: the latter stamps
            // LastRequestedAt on every servable band, which would make this "reader" a writer and
            // turn the measurement into writer-vs-writer lock queueing (see the #21 note above).
            var readerStore = new SearchResultCacheStore(readerContext);
            var i = readerIndex;

            startBarrier.SignalAndWait();
            while (!ShouldStop())
            {
                var key = queryKeys[i % queryKeys.Length];
                i++;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await readerStore.GetAsync(key);
                    stopwatch.Stop();
                    latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                catch (Exception e)
                {
                    failures.Add(e);
                }
            }
        }, TaskCreationOptions.LongRunning).Unwrap()).ToArray();

        // This test thread is the final barrier participant, so the measurement window opens only
        // once every reader and the writer are actually running past their connection-open cost.
        startBarrier.SignalAndWait();
        await Task.Delay(measureWindow);
        Volatile.Write(ref stopFlag, 1);

        await Task.WhenAll(readerThreads.Append(writerThread));

        var samples = latencies.ToList();
        Assert.True(samples.Count > 0, "No reader ever completed a single read - harness setup is broken.");

        return new ContentionOutcome(
            ReadCount: samples.Count,
            WriteCycleCount: Volatile.Read(ref writeCycleCount),
            MedianLatencyMs: Percentile(samples, 0.50),
            P99LatencyMs: Percentile(samples, 0.99),
            MaxLatencyMs: samples.Max(),
            Failures: failures.ToList());
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
