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
