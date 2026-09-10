using Microsoft.Data.Sqlite;

namespace Arbitarr.Data.Backup;

/// <summary>
/// Drops every pooled connection this assembly can hold on ONE database file, without touching any
/// other database's pools.
///
/// <para><b>What this replaces, and why the replacement is not a like-for-like swap.</b> The
/// restore path previously called <see cref="SqliteConnection.ClearAllPools"/>, which is
/// process-global: it force-closes every pooled connection in the process, including those of
/// unrelated databases — the log store's among them. That over-reach is the mechanism behind
/// arb-cbc/arb-5ba, where a pool clear in one place closed a handle another caller was still using.
/// Clearing per connection string keeps the guarantee the restore actually needs (no surviving
/// handle on the file about to be replaced) without the collateral damage (arb-n21).</para>
///
/// <para><b>Completeness is the whole property.</b> Pools are keyed by the full connection string,
/// so clearing one string leaves any other pool naming the same file untouched. Missing one is
/// silent in the worst way on Linux: the file swap SUCCEEDS, and the stale handle keeps serving the
/// replaced inode, so the process reads the old database while the restored one sits on disk
/// looking applied. That is why the set comes from
/// <see cref="DatabaseConnectionStrings.ForDatabase"/> rather than being listed here — one source,
/// which a new call site cannot bypass without failing
/// <c>NoInlineDatabaseConnectionStringsTests</c> (in <c>tests/Arbitarr.Architecture.Tests</c>): it
/// reads this assembly's IL and fails any type outside its named allow-list that constructs a
/// <c>SqliteConnectionStringBuilder</c> or a <c>SqliteConnection</c>.</para>
/// </summary>
public static class SqlitePoolCleaner
{
    /// <summary>
    /// Clears the connection pool for every string that names <paramref name="databasePath"/>.
    ///
    /// <para>Each pool is cleared through a throwaway <see cref="SqliteConnection"/> carrying that
    /// exact string: <see cref="SqliteConnection.ClearPool"/> selects the pool by the connection's
    /// own string, so the connection is the address, not a resource — it is never opened.</para>
    /// </summary>
    public static void ClearPoolsFor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        foreach (var connectionString in DatabaseConnectionStrings.ForDatabase(databasePath))
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }
}
