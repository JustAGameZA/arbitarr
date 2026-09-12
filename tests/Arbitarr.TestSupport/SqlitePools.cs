using Microsoft.Data.Sqlite;

namespace Arbitarr.TestSupport;

/// <summary>
/// Clears SQLite connection pools for ONE database at a time, for tests that own a config
/// DIRECTORY rather than a single file and so cannot use <see cref="SqliteTestDatabase"/>.
///
/// <para><b>Why not ClearAllPools.</b> <c>SqliteConnection.ClearAllPools()</c> is process-global:
/// it force-closes every pooled connection in the process, including ones belonging to a test class
/// running concurrently, which is what made parallel classes race each other (arb-cbc, arb-5ba:
/// <c>ObjectDisposedException</c> from a connection a neighbour closed). These helpers name the
/// database being torn down, so a neighbour's connections are left alone.</para>
///
/// <para><b>The connection string must match the one actually used, in full.</b> Pools are keyed by
/// the COMPLETE connection string, not by the file path: <c>Data Source=x.db</c> and
/// <c>Data Source=x.db;Cache=Default</c> name two different pools over one file. Passing a plainer
/// string than the code under test built clears a pool nobody filled — the cleanup then silently
/// stops working while still looking correct, and the <c>Directory.Delete</c> that follows goes
/// back to failing intermittently on Windows. Build the string the same way the production type
/// does, which is why <see cref="ClearLogStorePool"/> exists rather than callers guessing.</para>
/// </summary>
public static class SqlitePools
{
    /// <summary>
    /// Clears the pool for a database opened with a bare <c>Data Source=</c> string — what
    /// <c>BackupPaths.DatabasePath</c> consumers and EF's <c>UseSqlite($"Data Source={path}")</c>
    /// produce.
    ///
    /// <para>Correct only for callers that seeded the file through a raw
    /// <see cref="SqliteConnectionStringBuilder"/> with just <c>DataSource</c> set (e.g.
    /// <c>RestoreServiceTests</c>, <c>AutomaticBackupJobTests</c>). When the connection instead came
    /// from <c>SqliteConnectionFactory</c>, use <c>SqlitePoolCleaner.ClearPoolsFor</c> —
    /// pools are keyed by the full connection string, and that factory's string carries additional
    /// pragmas a bare <c>Data Source=</c> string does not, so this method would name an empty pool.</para>
    /// </summary>
    public static void ClearPoolForFile(string databasePath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// Clears the bare-<c>Data Source=</c> pool for every <c>*.db</c> file currently under
    /// <paramref name="directory"/>, for a test class that owns a config DIRECTORY.
    ///
    /// <para>Enumerating rather than naming the files is deliberate: such a directory holds the
    /// main database, the log database, and any file a test restored or extracted beside them, and
    /// a list spelled out here would silently stop covering a file a later test adds. The directory
    /// is per-class and per-run, so this still names only databases this class owns — the property
    /// <c>ClearAllPools</c> failed to have.</para>
    /// </summary>
    public static void ClearPoolsForDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var databasePath in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            ClearPoolForFile(databasePath);
            ClearLogStorePool(databasePath);
        }
    }

    /// <summary>
    /// Clears the pool for a <c>LogStore</c> database.
    ///
    /// <para>Separate from <see cref="ClearPoolForFile"/> because <c>LogStore</c> builds its
    /// connection string with Mode, Cache AND Pooling set, and the full string is the pool key — a
    /// bare <c>Data Source=</c> would name a different pool and clear nothing. Kept in step with
    /// <c>LogStore</c>'s own constructor.</para>
    /// </summary>
    public static void ClearLogStorePool(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString());
        SqliteConnection.ClearPool(connection);
    }
}
