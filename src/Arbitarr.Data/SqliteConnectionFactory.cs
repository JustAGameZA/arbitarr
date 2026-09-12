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
            ApplyBusyTimeout(connection);

            using var verifyCommand = connection.CreateCommand();
            verifyCommand.CommandText = "PRAGMA journal_mode;";
            VerifyJournalMode(verifyCommand.ExecuteScalar());
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private void ApplyBusyTimeout(SqliteConnection connection)
    {
        using var busyTimeoutCommand = connection.CreateCommand();
        busyTimeoutCommand.CommandText = $"PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};";
        busyTimeoutCommand.ExecuteNonQuery();
    }

    private static void VerifyJournalMode(object? result)
    {
        var mode = result as string;
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected SQLite journal_mode to be 'wal' but it reported '{mode ?? "<null>"}'. " +
                "WAL mode is required for AC15a (concurrent classifier writes must not block the " +
                "inline reader); refusing to proceed with an unverified journal mode. The one-time " +
                "conversion (SqliteConnectionFactory.ConvertToWalOnce) must run at startup before " +
                "any connection is opened.");
        }
    }
}
