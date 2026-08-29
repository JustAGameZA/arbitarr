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
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly SqliteConnectionOptions _options;

    public SqliteConnectionFactory(SqliteConnectionOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Opens a new <see cref="SqliteConnection"/>, applies <c>busy_timeout</c> and requests WAL
    /// journal mode, then verifies WAL actually took effect by querying <c>PRAGMA journal_mode</c>
    /// back. Throws <see cref="InvalidOperationException"/> if the database reports any journal
    /// mode other than "wal" — this is a runtime assertion, not just a fire-and-forget PRAGMA.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_options.ToConnectionString());
        connection.Open();

        try
        {
            ConfigureAndVerify(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private void ConfigureAndVerify(SqliteConnection connection)
    {
        using (var busyTimeoutCommand = connection.CreateCommand())
        {
            busyTimeoutCommand.CommandText = $"PRAGMA busy_timeout = {_options.BusyTimeoutMilliseconds};";
            busyTimeoutCommand.ExecuteNonQuery();
        }

        using (var journalModeCommand = connection.CreateCommand())
        {
            journalModeCommand.CommandText = "PRAGMA journal_mode = WAL;";
            var result = journalModeCommand.ExecuteScalar();
            VerifyJournalMode(result);
        }

        // Re-query journal_mode independently of the SET statement's own return value, so this
        // is a genuine runtime verification rather than trusting the pragma's immediate result.
        using (var verifyCommand = connection.CreateCommand())
        {
            verifyCommand.CommandText = "PRAGMA journal_mode;";
            var result = verifyCommand.ExecuteScalar();
            VerifyJournalMode(result);
        }
    }

    private static void VerifyJournalMode(object? result)
    {
        var mode = result as string;
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected SQLite journal_mode to be 'wal' but it reported '{mode ?? "<null>"}'. " +
                "WAL mode is required for AC15a (concurrent classifier writes must not block the " +
                "inline reader); refusing to proceed with an unverified journal mode.");
        }
    }
}
