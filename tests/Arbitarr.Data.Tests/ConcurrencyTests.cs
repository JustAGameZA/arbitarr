using System.Diagnostics;
using System.Linq;
using Arbitarr.Data;
using Microsoft.Data.Sqlite;

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

        using var stopSignal = new CancellationTokenSource(durationMilliseconds);
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        long readCount = 0;
        long writeCount = 0;

        // Multiple concurrent writers: this is the realistic AC15a shape (the background
        // classifier's continuous writes contending with each other and with reads), and it is
        // also what makes lock contention reliably observable — see the class-level note on why
        // a single lone writer against a passive reader rarely contends at all.
        var writerTasks = Enumerable.Range(0, writerCount).Select(writerIndex => Task.Run(() =>
        {
            using var writerConnection = openWriterConnection();
            var row = writerIndex * 1000;
            while (!stopSignal.IsCancellationRequested)
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
        })).ToArray();

        var readerTasks = Enumerable.Range(0, readerCount).Select(_ => Task.Run(() =>
        {
            using var readerConnection = openReaderConnection();
            while (!stopSignal.IsCancellationRequested)
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
        })).ToArray();

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
    /// "under some absolute ceiling." Both phases run back-to-back in this same test execution
    /// (same machine, same process, same moment) so the comparison is never confounded by
    /// run-to-run environment noise — a separate baseline run captured yesterday would prove
    /// nothing about today's hardware/load.
    /// </summary>
    [Fact]
    public void ReaderLatency_P95UnderContention_StaysWithinTwentyPercentOfUncontendedBaseline()
    {
        var factory = new SqliteConnectionFactory(new SqliteConnectionOptions
        {
            DatabasePath = _dbPath,
            BusyTimeoutMilliseconds = SqliteConnectionOptions.DefaultBusyTimeoutMilliseconds,
        });

        using (var setupConnection = factory.OpenConnection())
        using (var createCommand = setupConnection.CreateCommand())
        {
            createCommand.CommandText =
                "CREATE TABLE IF NOT EXISTS concurrency_probe (id INTEGER PRIMARY KEY, value TEXT NOT NULL);";
            createCommand.ExecuteNonQuery();
        }

        // Baseline phase: readers alone, no writers running, same connection factory/config as
        // the contended phase. This is the "same run" comparison point -- captured immediately
        // before contention starts, on the same machine load, so the 20% tolerance is not
        // swamped by unrelated environment variance.
        var baselineLatencies = RunReaderLatencyProbe(factory, TimeSpan.FromMilliseconds(1500), readerCount: 4);

        // Contended phase: identical reader probe, now racing 3 continuous BEGIN IMMEDIATE
        // writers -- the same writer shape as the existing WAL contention test.
        using var stopSignal = new CancellationTokenSource();
        var writerTasks = Enumerable.Range(0, 3).Select(writerIndex => Task.Run(() =>
        {
            using var writerConnection = factory.OpenConnection();
            var row = writerIndex * 1000;
            while (!stopSignal.IsCancellationRequested)
            {
                try
                {
                    using (var beginCommand = writerConnection.CreateCommand())
                    {
                        beginCommand.CommandTimeout = 10;
                        beginCommand.CommandText = "BEGIN IMMEDIATE;";
                        beginCommand.ExecuteNonQuery();
                    }

                    using (var insertCommand = writerConnection.CreateCommand())
                    {
                        insertCommand.CommandTimeout = 10;
                        insertCommand.CommandText =
                            "INSERT INTO concurrency_probe (id, value) VALUES ($id, $value) " +
                            "ON CONFLICT(id) DO UPDATE SET value = excluded.value;";
                        insertCommand.Parameters.AddWithValue("$id", row % 50);
                        insertCommand.Parameters.AddWithValue("$value", Guid.NewGuid().ToString());
                        insertCommand.ExecuteNonQuery();
                    }

                    Thread.Sleep(5);

                    using (var commitCommand = writerConnection.CreateCommand())
                    {
                        commitCommand.CommandTimeout = 10;
                        commitCommand.CommandText = "COMMIT;";
                        commitCommand.ExecuteNonQuery();
                    }

                    row++;
                }
                catch
                {
                    TryRollback(writerConnection);
                }
            }
        })).ToArray();

        List<double> contendedLatencies;
        try
        {
            contendedLatencies = RunReaderLatencyProbe(factory, TimeSpan.FromMilliseconds(1500), readerCount: 4);
        }
        finally
        {
            stopSignal.Cancel();
            Task.WaitAll(writerTasks);
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
    /// Runs trivial COUNT(*) reads back-to-back across <paramref name="readerCount"/> concurrent
    /// tasks for <paramref name="duration"/>, returning every individual query's wall-clock
    /// latency in milliseconds.
    /// </summary>
    private static List<double> RunReaderLatencyProbe(SqliteConnectionFactory factory, TimeSpan duration, int readerCount)
    {
        using var stopSignal = new CancellationTokenSource(duration);
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();

        var readerTasks = Enumerable.Range(0, readerCount).Select(_ => Task.Run(() =>
        {
            using var readerConnection = factory.OpenConnection();
            while (!stopSignal.IsCancellationRequested)
            {
                var stopwatch = Stopwatch.StartNew();
                using var selectCommand = readerConnection.CreateCommand();
                selectCommand.CommandTimeout = 10;
                selectCommand.CommandText = "SELECT COUNT(*) FROM concurrency_probe;";
                selectCommand.ExecuteScalar();
                stopwatch.Stop();
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        })).ToArray();

        Task.WaitAll(readerTasks);

        Assert.True(latencies.Count > 10, "Reader latency probe collected too few samples to compute a meaningful p95.");
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
