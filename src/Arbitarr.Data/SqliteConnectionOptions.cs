namespace Arbitarr.Data;

/// <summary>
/// Configurable connection-level settings for arr-searcher's SQLite database. These are
/// runtime/DI-level knobs (Step 2, worker-2's scope) — distinct from the persisted
/// application settings table (<see cref="Entities.SettingEntry"/>, worker-3's scope).
/// </summary>
public sealed class SqliteConnectionOptions
{
    /// <summary>
    /// Default <c>busy_timeout</c> in milliseconds applied to every connection this factory
    /// opens, unless overridden. 5000ms is chosen so a writer holding the SQLite write lock for
    /// a normal single-row insert/update (sub-millisecond to low tens of ms in practice) never
    /// causes a concurrent reader to fail with SQLITE_BUSY; in WAL mode readers do not block on
    /// writers at all, so this timeout only matters for the rarer case of a second concurrent
    /// writer (e.g. the maintenance job's prune/vacuum) contending with the classifier's writes.
    /// </summary>
    public const int DefaultBusyTimeoutMilliseconds = 5000;

    /// <summary>
    /// Full path to the SQLite database file (e.g. under <c>/config</c> in production). Required.
    /// </summary>
    public required string DatabasePath { get; init; }

    /// <summary>
    /// <c>busy_timeout</c> in milliseconds: how long SQLite will silently retry before returning
    /// SQLITE_BUSY when a connection cannot immediately acquire the lock it needs. Configurable
    /// (not hardcoded) so operators can raise it under heavier contention. Defaults to
    /// <see cref="DefaultBusyTimeoutMilliseconds"/>.
    /// </summary>
    public int BusyTimeoutMilliseconds { get; init; } = DefaultBusyTimeoutMilliseconds;

    /// <summary>
    /// Builds the ADO.NET connection string for this configuration. WAL mode itself is not
    /// expressible purely via connection-string keywords in a way that's guaranteed to be
    /// verified, so it is set (and verified) explicitly by <see cref="SqliteConnectionFactory"/>
    /// after opening — this string only carries the busy timeout and cache mode.
    /// </summary>
    public string ToConnectionString()
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Default,
        };

        return builder.ToString();
    }
}
