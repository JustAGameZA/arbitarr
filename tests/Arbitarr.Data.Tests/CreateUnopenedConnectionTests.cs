using Arbitarr.Data.Backup;
using Arbitarr.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-ttlq: unit-covers <see cref="SqliteConnectionFactory.CreateUnopenedConnection"/> — the path
/// added by arb-auam so a resolved-but-unused <c>DbContext</c> cannot leak a handle it never
/// adopted. <see cref="WalConversionBeforeConcurrentOpensTests"/> covers the same factory's
/// <c>OpenConnection</c>/<c>ConvertToWalOnce</c> split (arb-itmm); this class covers the deferred
/// twin, which nothing exercised directly.
///
/// <para><b>What is actually at risk here, and therefore what is asserted.</b> The configuration is
/// hung off <see cref="System.Data.Common.DbConnection.StateChange"/>, which is an EVENT: an
/// exception thrown from a handler is the kind of thing a provider may reasonably swallow, log, or
/// re-raise. The factory's remarks state it surfaces out of the caller's own <c>Open()</c> — that
/// is the load-bearing claim, because it is what makes the WAL verification still fail CLOSED when
/// EF, not this type, does the opening. So the central test drives a non-WAL file and requires the
/// throw to come out of <c>Open()</c>. Were Microsoft.Data.Sqlite to start swallowing it in some
/// future version, EF would silently run against an unverified journal mode and nothing else in the
/// suite would notice.</para>
///
/// <para><b>And the documented ASYMMETRY is pinned, not merely described.</b>
/// <c>OpenConnection</c> disposes on a failed verification; the handler cannot, because it runs
/// inside the caller's <c>Open()</c>. The factory's remarks say so at length, and a later "tidy-up"
/// that made the handler dispose (or that moved the configuration off the event) would contradict
/// the documentation while every other test still passed. Asserting the post-throw STATE on both
/// paths is what turns that prose into a fact the build checks.</para>
///
/// <para>No <c>ClearAllPools</c> (CLAUDE.md section 4): teardown goes through
/// <see cref="SqliteTestDatabase"/>, whose scoped clear is keyed by connection string — and the
/// factory's string is registered with it below, since it differs from the fixture's bare one and
/// so names a second pool.</para>
/// </summary>
public sealed class CreateUnopenedConnectionTests : IDisposable
{
    /// <summary>
    /// Deliberately NOT the default. A test that asserted the default value would pass against a
    /// factory that ignored the options entirely and let SQLite's own configuration stand, so the
    /// value has to be one only this factory could have produced.
    /// </summary>
    private const int ProbeBusyTimeoutMilliseconds = 3571;

    private readonly SqliteTestDatabase _database = new("arbitarr-ttlq-unopened");

    public CreateUnopenedConnectionTests()
    {
        // SqliteConnectionOptions.ToConnectionString() emits "Cache=Default" and so names a
        // different pool than the fixture's own bare "Data Source=" string. Registering it is what
        // lets the fixture's dispose clear the pool these tests actually fill.
        _database.AlsoClearPoolFor(ConnectionOptions.ToConnectionString());
    }

    private SqliteConnectionOptions ConnectionOptions => new()
    {
        DatabasePath = _database.Path,
        BusyTimeoutMilliseconds = ProbeBusyTimeoutMilliseconds,
    };

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// The closed state is the whole point of the method (arb-auam): EF's
    /// <c>RelationalConnection</c> only ADOPTS a connection the first time the context uses it, so
    /// an already-open one handed to an unused context is never released.
    /// </summary>
    [Fact]
    public void CreateUnopenedConnection_returns_a_connection_that_is_not_yet_open()
    {
        var factory = new SqliteConnectionFactory(ConnectionOptions);
        factory.ConvertToWalOnce();

        using var connection = factory.CreateUnopenedConnection();

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

        // POSITIVE CONTROL for the assertion above: this connection is a usable one that simply has
        // not been opened yet, not an inert object that could never reach Open. Without this, a
        // method returning something permanently broken would satisfy the Closed assertion.
        connection.Open();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    /// <summary>
    /// The pragmas must be applied on the CALLER's open, not at construction — that is the
    /// difference between this path and <c>OpenConnection</c>, and the only way EF's opens get
    /// configured at all.
    ///
    /// <para>The expected value is MEASURED off <c>OpenConnection</c> rather than hard-coded, so the
    /// two paths are compared to each other. That is the property the factory's class doc promises
    /// (one private <c>Configure</c>, no drift), and comparing against a literal would let both
    /// paths regress together undetected.</para>
    ///
    /// <para><b>POOLED HANDLES MAKE THE OBVIOUS VERSION OF THIS TEST UNKILLABLE, and the two
    /// defences below are what close that.</b> <c>busy_timeout</c> is connection-scoped state, and a
    /// handle returned to the pool KEEPS it. So any earlier factory call on the same file — even
    /// <c>ConvertToWalOnce</c>, which applies the timeout on a connection of its own — leaves a
    /// pooled handle already carrying the value, and the <c>Open()</c> under test simply draws that
    /// handle and reads back the right answer having configured nothing. Measured, not theorised:
    /// with the reference taken on this fixture's file, deleting the <c>StateChange</c>
    /// registration outright left this test PASSING, and it still passed with the reference moved
    /// to a second file, because the conversion alone was enough to seed the pool.</para>
    ///
    /// <para>Hence: the reference is measured on a SECOND DATABASE FILE (a distinct pool, so the
    /// reference's own handle is unreachable from here), AND this file's pool is CLEARED after the
    /// conversion, so the connection under test can only be opened from scratch. As this test is
    /// ORDERED, the per-file clear is what actually closes the vacuum: it runs after both the
    /// reference work and this file's own <c>ConvertToWalOnce</c>, so it is what forces the open
    /// under test to configure a fresh handle rather than draw the pooled one. The second-file
    /// reference is not decoration either, but for a different reason: order aside, it keeps the
    /// reference's own handle on a pool this test's clear cannot touch, so a reference taken on this
    /// same file would still be reachable and would seed the same false pass regardless of ordering.
    /// The clear goes through <c>SqlitePoolCleaner</c>, the scoped per-file API; CLAUDE.md section 4
    /// forbids <c>ClearAllPools</c>, which would reach into neighbouring test classes.</para>
    /// </summary>
    [Fact]
    public void The_busy_timeout_is_applied_when_the_caller_opens_the_connection()
    {
        int expected;
        using (var reference = new SqliteTestDatabase("arbitarr-ttlq-busy-timeout-reference"))
        {
            var referenceOptions = new SqliteConnectionOptions
            {
                DatabasePath = reference.Path,
                BusyTimeoutMilliseconds = ProbeBusyTimeoutMilliseconds,
            };
            reference.AlsoClearPoolFor(referenceOptions.ToConnectionString());

            var referenceFactory = new SqliteConnectionFactory(referenceOptions);
            referenceFactory.ConvertToWalOnce();

            using var opened = referenceFactory.OpenConnection();
            expected = ReadBusyTimeout(opened);
        }

        // Not a tautology: this asserts the measured reference really is the configured value, so a
        // factory that applied nothing on EITHER path could not make the comparison below pass by
        // agreeing on SQLite's default.
        Assert.Equal(ProbeBusyTimeoutMilliseconds, expected);

        var factory = new SqliteConnectionFactory(ConnectionOptions);
        factory.ConvertToWalOnce();

        // The conversion just pooled a handle carrying busy_timeout. Drop it, or the open below
        // inherits the pragma instead of applying it — see this test's remarks.
        SqlitePoolCleaner.ClearPoolsFor(_database.Path);

        using var connection = factory.CreateUnopenedConnection();
        connection.Open();

        Assert.Equal(expected, ReadBusyTimeout(connection));
    }

    /// <summary>
    /// THE CENTRAL TEST. Against a rollback-journal file the WAL verification fails, and the
    /// failure must surface out of the CALLER's <c>Open()</c> rather than being swallowed by the
    /// provider's event dispatch — otherwise EF would proceed on an unverified journal mode.
    ///
    /// <para>POSITIVE CONTROL (CLAUDE.md section 4): the same file opened through
    /// <c>OpenConnection</c> — whose throw is plain synchronous control flow, with no event
    /// dispatch between the verification and the caller — must produce the SAME failure. That is
    /// what demonstrates the file is genuinely in a state the verification rejects, so the
    /// deferred path's throw is a real detection rather than an artefact.</para>
    ///
    /// <para>The post-throw STATE is asserted on both paths because the two deliberately differ,
    /// and the difference is documented on <c>CreateUnopenedConnection</c> rather than enforced
    /// anywhere: the handler runs inside the caller's <c>Open()</c> and so cannot dispose the
    /// connection, leaving it Open, while <c>OpenConnection</c> disposes on the way out. Pinning
    /// both is what stops a refactor from quietly changing either one.</para>
    /// </summary>
    [Fact]
    public void A_non_wal_file_makes_the_verification_throw_out_of_the_callers_own_Open()
    {
        SeedRollbackJournalDatabase();

        var factory = new SqliteConnectionFactory(ConnectionOptions);

        // POSITIVE CONTROL: the raw path rejects this file, so the rejection below is real.
        var control = Assert.Throws<InvalidOperationException>(() => factory.OpenConnection());
        Assert.Contains("journal_mode", control.Message, StringComparison.Ordinal);
        Assert.Contains("delete", control.Message, StringComparison.OrdinalIgnoreCase);

        using var connection = factory.CreateUnopenedConnection();

        var deferred = Assert.Throws<InvalidOperationException>(() => connection.Open());
        Assert.Contains("journal_mode", deferred.Message, StringComparison.Ordinal);

        // The message names the mode it actually found, which is what makes it actionable in
        // /api/admin/logs — and what proves the verification read the file rather than failing for
        // some unrelated reason that happens to share the exception type.
        Assert.Contains("delete", deferred.Message, StringComparison.OrdinalIgnoreCase);

        // The documented asymmetry (arb-auam), pinned. The StateChange handler cannot dispose the
        // connection it is running inside, so this one is left Open even though its journal mode
        // was never verified; nothing leaks, because EF disposes it under contextOwnsConnection.
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    /// <summary>
    /// The other half of the asymmetry, separated so each side fails on its own terms: the raw path
    /// leaves NO usable handle behind, because <c>OpenConnection</c> disposes before rethrowing.
    ///
    /// <para>Asserted on a connection this test cannot reach directly — <c>OpenConnection</c> never
    /// returns one when it throws — so the observation is indirect: the failed open must not have
    /// left a pooled or live handle, which the fixture's teardown would otherwise race. Instead the
    /// disposal is shown by the file remaining deletable-and-reopenable in the same DELETE mode,
    /// i.e. the rejected open changed and retained nothing.</para>
    /// </summary>
    [Fact]
    public void The_raw_open_path_retains_nothing_after_a_failed_verification()
    {
        SeedRollbackJournalDatabase();

        var factory = new SqliteConnectionFactory(ConnectionOptions);

        Assert.Throws<InvalidOperationException>(() => factory.OpenConnection());

        // Still DELETE mode: the rejected open verified rather than converted (arb-itmm), and the
        // file is reachable again, so the connection it built is not still holding it.
        //
        // Deliberately raw, not the factory: this reopen must not re-run the factory's own WAL
        // verification, or it would throw on the very file this check is confirming is reachable.
        // NoInlineDatabaseConnectionStringsTests scans Arbitarr.Data only, so this test project is
        // outside its reach — a tidy-up must not route this through the factory regardless.
        using var after = new SqliteConnection(_database.ConnectionString);
        after.Open();
        Assert.Equal("delete", ReadJournalMode(after));
    }

    /// <summary>
    /// Creates the file in SQLite's default rollback-journal mode WITHOUT the factory, so the
    /// verification has something to reject. A table is created because an empty file has no header
    /// to report a journal mode from.
    /// </summary>
    private void SeedRollbackJournalDatabase()
    {
        // Deliberately raw, not the factory: the factory converts to WAL on open, and this helper's
        // whole job is to leave the file in SQLite's default rollback-journal mode for the
        // verification to reject. NoInlineDatabaseConnectionStringsTests scans Arbitarr.Data only,
        // so this test project is outside its reach — a tidy-up must not route this through the
        // factory regardless.
        using var seed = new SqliteConnection(_database.ConnectionString);
        seed.Open();

        using var create = seed.CreateCommand();
        create.CommandText = "CREATE TABLE ttlq_probe(id INTEGER PRIMARY KEY);";
        create.ExecuteNonQuery();

        Assert.Equal("delete", ReadJournalMode(seed));
    }

    private static int ReadBusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ReadJournalMode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return command.ExecuteScalar() as string ?? "<null>";
    }
}
