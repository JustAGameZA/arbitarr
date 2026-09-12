using System.Diagnostics;
using Arbitarr.Data;
using Arbitarr.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-itmm: proves the split between CONVERTING the journal mode and VERIFYING it.
///
/// <para><b>The defect.</b> <c>SqliteConnectionFactory.OpenConnection</c> used to issue
/// <c>PRAGMA journal_mode = WAL</c> on every open. On a FRESH database file with another connection
/// holding a transaction, that pragma is NOT bounded by <c>busy_timeout</c> — it was measured
/// waiting 8087 ms against a 5000 ms timeout, and not returning at all while the blocker held on.
/// Since it always eventually answers <c>wal</c>, it never produced a WRONG answer, only an
/// unbounded WAIT: a container's first start, with several hosted services opening the new
/// <c>arbitarr.db</c> at once, could hang with no timeout anywhere to break it.</para>
///
/// <para><b>What is asserted, and what is deliberately NOT.</b> These tests assert the FIX's shape —
/// that one conversion up front makes N concurrent opens succeed, and that the open path no longer
/// carries the conversion at all. They do NOT attempt to reproduce the 8 s hang: that repro needs a
/// blocker held across a real conversion and is inherently load-sensitive, which is the shape
/// arb-1ypa warns against. The wall-clock bound below is deliberately generous for the same reason —
/// it is there to catch an UNBOUNDED wait (which no bound would satisfy), not to measure latency.
/// No retries, no <c>ClearAllPools</c>, no Quarantine trait.</para>
/// </summary>
public sealed class WalConversionBeforeConcurrentOpensTests : IDisposable
{
    private const int ConcurrentOpeners = 16;

    private readonly SqliteTestDatabase _database = new("arbitarr-itmm-wal-conversion");

    public WalConversionBeforeConcurrentOpensTests()
    {
        // SqliteConnectionOptions.ToConnectionString() emits "Cache=Default" and so names a
        // different pool than the fixture's own bare "Data Source=" string. Registering it is what
        // lets the fixture's dispose clear the pool these tests actually fill.
        _database.AlsoClearPoolFor(ConnectionOptions.ToConnectionString());
    }

    private SqliteConnectionOptions ConnectionOptions => new()
    {
        DatabasePath = _database.Path,
        BusyTimeoutMilliseconds = SqliteConnectionOptions.DefaultBusyTimeoutMilliseconds,
    };

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task After_the_one_time_conversion_concurrent_opens_on_a_fresh_file_all_succeed_within_a_bounded_time()
    {
        var factory = new SqliteConnectionFactory(ConnectionOptions);

        // The single, non-concurrent conversion — exactly what Program.cs does before the migration
        // scope and before any hosted service starts.
        factory.ConvertToWalOnce();

        var barrier = new Barrier(ConcurrentOpeners);
        var stopwatch = Stopwatch.StartNew();

        var opens = Enumerable.Range(0, ConcurrentOpeners)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    // Release every thread at once, so the opens genuinely overlap rather than
                    // trickling through one at a time as the pool warms.
                    barrier.SignalAndWait();

                    using var connection = factory.OpenConnection();
                    return ReadJournalMode(connection);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        var modes = await Task.WhenAll(opens);
        stopwatch.Stop();

        // POSITIVE CONTROL: every connection must actually report wal. Without this the timing
        // assertion below would pass just as happily against a database that never converted —
        // an open that does no WAL work is trivially fast.
        Assert.All(modes, mode => Assert.Equal("wal", mode));
        Assert.Equal(ConcurrentOpeners, modes.Length);

        // Generous on purpose: the failure this guards against is an UNBOUNDED wait, which no
        // ceiling satisfies. A tight bound here would measure the runner, not the code.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"{ConcurrentOpeners} concurrent opens took {stopwatch.ElapsedMilliseconds} ms, which " +
            "suggests the WAL conversion is happening on the open path again rather than once up " +
            "front — that pragma is not bounded by busy_timeout on a fresh file.");
    }

    /// <summary>
    /// The open path must VERIFY the journal mode and never set it. Driving it against a file left
    /// in DELETE mode is the direct proof: before arb-itmm this open would have silently CONVERTED
    /// the file and returned a working connection, so this test failing to throw means the
    /// conversion has crept back onto the hot path.
    /// </summary>
    [Fact]
    public void OpenConnection_verifies_the_journal_mode_rather_than_converting_it()
    {
        // Create the file in SQLite's default rollback-journal mode, without the factory.
        using (var seed = new SqliteConnection(_database.ConnectionString))
        {
            seed.Open();
            using var create = seed.CreateCommand();
            create.CommandText = "CREATE TABLE itmm_probe(id INTEGER PRIMARY KEY);";
            create.ExecuteNonQuery();

            Assert.Equal("delete", ReadJournalMode(seed));
        }

        var factory = new SqliteConnectionFactory(ConnectionOptions);

        var failure = Assert.Throws<InvalidOperationException>(() => factory.OpenConnection());
        Assert.Contains("journal_mode", failure.Message, StringComparison.Ordinal);
        Assert.Contains("delete", failure.Message, StringComparison.OrdinalIgnoreCase);

        // And the file is still in DELETE mode afterwards: the rejected open changed nothing.
        using (var after = new SqliteConnection(_database.ConnectionString))
        {
            after.Open();
            Assert.Equal("delete", ReadJournalMode(after));
        }

        // The paired half: the same factory, after the explicit conversion, opens cleanly. Without
        // this the test above would also pass against a factory that simply always threw.
        factory.ConvertToWalOnce();

        using var converted = factory.OpenConnection();
        Assert.Equal("wal", ReadJournalMode(converted));
    }

    /// <summary>
    /// The failure message may reach the persistent log store, so it must not carry the config
    /// directory (or any other filesystem path) with it.
    /// </summary>
    [Fact]
    public void The_unverified_journal_mode_failure_does_not_disclose_the_database_path()
    {
        using (var seed = new SqliteConnection(_database.ConnectionString))
        {
            seed.Open();
            using var create = seed.CreateCommand();
            create.CommandText = "CREATE TABLE itmm_probe(id INTEGER PRIMARY KEY);";
            create.ExecuteNonQuery();
        }

        var factory = new SqliteConnectionFactory(ConnectionOptions);
        var failure = Assert.Throws<InvalidOperationException>(() => factory.OpenConnection());

        // POSITIVE CONTROL for the absence assertions below: the path and its directory are
        // non-empty and genuinely distinctive, so "not present" is a real fact about the message
        // rather than a vacuous search for nothing.
        var directory = Path.GetDirectoryName(_database.Path);
        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.Contains("arbitarr-itmm-wal-conversion", _database.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("arbitarr-itmm-wal-conversion", failure.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(_database.Path, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(directory!, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadJournalMode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return command.ExecuteScalar() as string ?? "<null>";
    }
}
