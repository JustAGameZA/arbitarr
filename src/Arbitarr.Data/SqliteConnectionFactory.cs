using Microsoft.Data.Sqlite;

namespace Arbitarr.Data;

/// <summary>
/// Opens SQLite connections configured for arr-searcher's persistence foundation (Step 2):
/// WAL journal mode and an explicit <c>busy_timeout</c>, so the background classifier's
/// continuous writes cannot block the inline reader on the D1-critical request path (AC15a).
///
/// Both the app (via DI, wired in the Host composition root) and tests should open connections
/// through this factory rather than constructing <see cref="SqliteConnection"/> directly, so the
/// pragma configuration is applied consistently everywhere.
///
/// <para><b>The WAL CONVERSION and the WAL VERIFICATION are deliberately two different calls
/// (arb-itmm).</b> <see cref="ConvertToWalOnce"/> is the only place that may CHANGE the journal
/// mode, and it must be called exactly once, before anything opens this database concurrently.
/// <see cref="OpenConnection"/> then only READS the mode back. Do not fold the conversion back
/// into the open path: see the remarks on <see cref="ConvertToWalOnce"/>.</para>
///
/// <para><b>Two ways to get a connection, differing only in WHO opens it.</b>
/// <see cref="OpenConnection"/> returns one already open and configured, for callers that use it
/// immediately. <see cref="CreateUnopenedConnection"/> returns a CLOSED one that configures itself
/// when its eventual opener opens it — that is what EF Core is given, so a resolved-but-unused
/// <c>DbContext</c> cannot leak a handle it never adopted (arb-auam). Both route the pragmas
/// through the same private <c>Configure</c>, so neither path can drift from the other.</para>
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly SqliteConnectionOptions _options;

    public SqliteConnectionFactory(SqliteConnectionOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Converts the database file to WAL journal mode, once, on a connection of its own — and
    /// applies the file-creation-time <c>auto_vacuum</c> setting at the same time. Call this
    /// exactly once at startup, BEFORE any concurrent opener exists; the Host does so immediately
    /// before the migration scope, and the integration test factories inherit that by running the
    /// same composition root.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this cannot live on the open path (arb-itmm, measured).</b> On a FRESH database
    /// file with another connection holding a transaction, <c>PRAGMA journal_mode = WAL</c> is NOT
    /// bounded by <c>busy_timeout</c>: it was observed waiting 8087 ms against a 5000 ms timeout,
    /// and with the blocker never releasing it did not return at all. Because the pragma always
    /// eventually answers <c>wal</c>, this never surfaces as a wrong answer — only as an unbounded
    /// WAIT, which on a container's first start (several hosted services opening the new
    /// <c>arbitarr.db</c> at once) is a hang with no timeout anywhere to break it.</para>
    ///
    /// <para><b>Why once is enough.</b> <c>journal_mode</c> is persisted in the database file
    /// header, not per connection. Measured on the real shape: after this call the file reports
    /// <c>wal</c> on every subsequent open, and re-issuing the SET against an already-WAL file is a
    /// no-op that completes in single-digit milliseconds even while another connection holds a
    /// write transaction. So the hazard is entirely confined to the not-yet-converted file, and
    /// converting before concurrency begins removes it rather than bounding it.</para>
    ///
    /// <para>The <c>auto_vacuum</c> pragma moves here for the same reason it was on the open path:
    /// it only takes effect before any table exists, so it must run on the connection that creates
    /// the file. That is now this one, which runs before the migration opens its own (M7-3a/AC22) —
    /// otherwise <c>MaintenanceJob</c>'s later <c>PRAGMA incremental_vacuum;</c> calls are silently
    /// inert.</para>
    /// </remarks>
    public void ConvertToWalOnce()
    {
        using var connection = new SqliteConnection(_options.ToConnectionString());
        connection.Open();

        ApplyBusyTimeout(connection);

        using (var autoVacuumCommand = connection.CreateCommand())
        {
            autoVacuumCommand.CommandText = "PRAGMA auto_vacuum = INCREMENTAL;";
            autoVacuumCommand.ExecuteNonQuery();
        }

        using (var journalModeCommand = connection.CreateCommand())
        {
            journalModeCommand.CommandText = "PRAGMA journal_mode = WAL;";
            var result = journalModeCommand.ExecuteScalar();
            VerifyJournalMode(result);
        }

        // Re-query journal_mode independently of the SET statement's own return value, so this
        // is a genuine verification rather than trusting the pragma's immediate result.
        using (var verifyCommand = connection.CreateCommand())
        {
            verifyCommand.CommandText = "PRAGMA journal_mode;";
            var result = verifyCommand.ExecuteScalar();
            VerifyJournalMode(result);
        }
    }

    /// <summary>
    /// Opens a new <see cref="SqliteConnection"/>, applies <c>busy_timeout</c>, then verifies the
    /// database is in WAL journal mode by querying <c>PRAGMA journal_mode</c>. Throws
    /// <see cref="InvalidOperationException"/> if it reports any mode other than "wal" — this is a
    /// runtime assertion, not just a fire-and-forget PRAGMA.
    ///
    /// <para>This path READS the journal mode and never sets it; <see cref="ConvertToWalOnce"/>
    /// must have run first. That split is arb-itmm's fix and is load-bearing — restoring the SET
    /// here reintroduces an unbounded wait on a fresh file under concurrent opens.</para>
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_options.ToConnectionString());
        connection.Open();

        try
        {
            Configure(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    /// <summary>
    /// Builds a connection in the <see cref="System.Data.ConnectionState.Closed"/> state that
    /// applies exactly what <see cref="OpenConnection"/> applies — <c>busy_timeout</c>, then the WAL
    /// verification — at the moment WHOEVER opens it does so, rather than here.
    ///
    /// <para><b>That equivalence is PER OPEN, not per connection.</b> EF closes and reopens an
    /// externally supplied <see cref="System.Data.Common.DbConnection"/> between operations, so this
    /// connection is opened many times over its life and <see cref="Configure"/> runs once per open
    /// — which is the correct granularity, since <c>busy_timeout</c> is connection-scoped state that
    /// a close discards and so must be reapplied each time. <see cref="OpenConnection"/> configures
    /// once because it hands back a connection it opened once; the two agree on what is applied to
    /// an open connection, not on how many times it happens.</para>
    ///
    /// <para><b>This exists for EF Core, and the closed state is the whole point (arb-auam).</b>
    /// <c>UseSqlite(DbConnection, contextOwnsConnection: true)</c> makes the context dispose the
    /// connection, but EF's <c>RelationalConnection</c> only ADOPTS a connection the first time the
    /// context actually uses it. Hand it one that is already OPEN and a context that is resolved and
    /// disposed without ever being used never adopts the handle: it is neither closed nor returned
    /// to the pool, so it survives every <c>ClearPool</c> and the process holds the file for its
    /// lifetime. Handing over a CLOSED connection removes the asymmetry — an unused context has
    /// nothing to release, and a used one is opened, owned and returned by EF itself.</para>
    ///
    /// <para><b>Why the configuration hangs off <see cref="System.Data.Common.DbConnection.StateChange"/>.</b> The
    /// pragmas can only run on an open connection, and by construction this method does not open it,
    /// so the work has to follow the open wherever it happens — inside EF, on a schedule this type
    /// does not control. The event is that hook, and routing it through the same
    /// <see cref="Configure"/> call <see cref="OpenConnection"/> uses is what keeps the single
    /// configuration path the class doc promises: EF and raw ADO.NET cannot drift, because there is
    /// one implementation and both reach it.</para>
    ///
    /// <para><b>Rejected alternative: <c>UseSqlite(connectionString)</c>.</b> It is the smaller
    /// change and it fixes the leak, because EF then creates and owns the connection outright. It
    /// also silently drops both pragmas — EF would open a connection this factory never configures,
    /// so <c>busy_timeout</c> would fall back to SQLite's default of 0 (immediate SQLITE_BUSY, the
    /// AC15a hazard) and the journal-mode verification would never run on the application's own
    /// connections. That is the drift the class doc exists to prevent, so the connection stays
    /// factory-built and only its OPENING moves.</para>
    ///
    /// <para>A throw from the handler surfaces out of the caller's <c>Open()</c> — measured, so the
    /// verification still fails closed through EF rather than being swallowed.</para>
    ///
    /// <para><b>It fails closed more WEAKLY than the raw path, though, and that asymmetry is
    /// deliberate rather than overlooked.</b> <see cref="OpenConnection"/> disposes the connection
    /// when <see cref="Configure"/> throws, so a failed verification leaves no usable handle behind.
    /// The <see cref="System.Data.Common.DbConnection.StateChange"/> handler cannot do that — it runs
    /// during the caller's own <c>Open()</c>, so disposing the connection from inside it is not
    /// available — and the connection is therefore left <see cref="System.Data.ConnectionState.Open"/>.
    /// <c>busy_timeout</c> has already been applied by that point and EF still disposes the
    /// connection under <c>contextOwnsConnection: true</c>, so nothing leaks; but a caller that
    /// caught the throw could go on using a handle whose journal mode was never verified. Only the
    /// verification is weaker, and only for a caller that swallows the exception.</para>
    /// </summary>
    public SqliteConnection CreateUnopenedConnection()
    {
        var connection = new SqliteConnection(_options.ToConnectionString());

        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState == System.Data.ConnectionState.Open)
            {
                Configure(connection);
            }
        };

        return connection;
    }

    /// <summary>
    /// Applies <c>busy_timeout</c> and verifies the journal mode on an already-open connection. The
    /// one place either happens, so <see cref="OpenConnection"/> and the deferred configuration of
    /// <see cref="CreateUnopenedConnection"/> cannot diverge.
    ///
    /// <para>This READS the journal mode and never sets it — see the remarks on
    /// <see cref="ConvertToWalOnce"/> for why that split (arb-itmm) is load-bearing.</para>
    /// </summary>
    private void Configure(SqliteConnection connection)
    {
        ApplyBusyTimeout(connection);

        using var verifyCommand = connection.CreateCommand();
        verifyCommand.CommandText = "PRAGMA journal_mode;";
        VerifyJournalMode(verifyCommand.ExecuteScalar());
    }

    private void ApplyBusyTimeout(SqliteConnection connection)
    {
        using var busyTimeoutCommand = connection.CreateCommand();
        busyTimeoutCommand.CommandText = $"PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};";
        busyTimeoutCommand.ExecuteNonQuery();
    }

    /// <remarks>
    /// The exception message is written for the operator reading it off <c>/api/admin/logs</c> or
    /// container output, not the developer reading this source: it tells them to restart the
    /// container, since <see cref="ConvertToWalOnce"/> — the one-time conversion this depends on —
    /// is not something they can invoke directly. Developers debugging why the conversion did not
    /// run belong in this XML doc and in <see cref="ConvertToWalOnce"/>'s remarks, not the message.
    /// </remarks>
    private static void VerifyJournalMode(object? result)
    {
        var mode = result as string;
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected SQLite journal_mode to be 'wal' but it reported '{mode ?? "<null>"}'. " +
                "WAL mode is required for AC15a (concurrent classifier writes must not block the " +
                "inline reader); refusing to proceed with an unverified journal mode. Restart the " +
                "container, which converts the database file to WAL mode once at startup before " +
                "any connection is opened.");
        }
    }
}
