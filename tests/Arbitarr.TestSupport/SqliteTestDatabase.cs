using Microsoft.Data.Sqlite;

namespace Arbitarr.TestSupport;

/// <summary>
/// Owns one temporary SQLite database file for the lifetime of a single test class, and tears it
/// down without touching any other test's state.
///
/// <para><b>WHY THIS TYPE EXISTS, AND WHY IT OWNS THE CONNECTION STRING.</b> Test classes used to
/// end with <c>SqliteConnection.ClearAllPools()</c> followed by <c>File.Delete</c>. That call is
/// PROCESS-GLOBAL: it force-closes every pooled connection in the process, including ones belonging
/// to a class running concurrently, which is what made parallel test classes race each other
/// (arb-cbc, arb-5ba: <c>ObjectDisposedException</c> from a connection a neighbour closed).</para>
///
/// <para>The scoped replacement is <see cref="SqliteConnection.ClearPool"/>, but it is keyed by a
/// connection instance — and the tests that need it hold none, because they hand a connection
/// STRING to EF (<c>optionsBuilder.UseSqlite($"Data Source={path}")</c>) and EF opens and owns the
/// connection internally. That is precisely why an earlier scoped-clearing attempt regressed with
/// file-in-use on dispose: with nothing to pass to <c>ClearPool</c>, EF's pooled connections stayed
/// open and the <c>File.Delete</c> that followed failed.</para>
///
/// <para>So this type owns the path and hands out <see cref="ConnectionString"/>. Because pools are
/// keyed by connection string, clearing the pool for a throwaway connection built from THAT string
/// clears exactly the pool EF filled. A test that builds its own connection string instead of
/// taking <see cref="ConnectionString"/> from here is clearing a different pool than it is using,
/// and the cleanup silently stops working — so take the string from the fixture.</para>
///
/// <para><b>Not for factory-hosted tests.</b> A <c>WebApplicationFactory</c> host owns its own
/// database file underneath the directory it is given via <c>Arbitarr:ConfigDir</c>, and disposes
/// of it itself. Such tests must NOT wrap the host's database in this fixture; use it only for a
/// database the test itself opens.</para>
/// </summary>
public sealed class SqliteTestDatabase : IDisposable
{
    private readonly List<string> _extraConnectionStrings = new();
    private bool _disposed;

    /// <param name="prefix">
    /// Distinguishes this class's temp files on disk when a run is being inspected by hand; it has
    /// no bearing on isolation, which comes from the GUID.
    /// </param>
    public SqliteTestDatabase(string prefix = "arbitarr-test")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}.db");

        ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path }.ToString();
    }

    /// <summary>Absolute path to this fixture's database file.</summary>
    public string Path { get; }

    /// <summary>
    /// The connection string every consumer of this database must use. Taking it from here rather
    /// than rebuilding it is what keeps <see cref="Dispose"/>'s scoped pool clear pointed at the
    /// pool actually in use — see the type's remarks.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Registers an ADDITIONAL connection string that also names this fixture's file, so its pool
    /// is cleared alongside <see cref="ConnectionString"/> on dispose.
    ///
    /// <para>Pools are keyed by the FULL connection string, not by the file: a test that reaches
    /// the same database through a second string (a different <c>Cache</c>, <c>Mode</c> or
    /// <c>Pooling</c> setting — <c>SqliteConnectionOptions.ToConnectionString</c> and a bare
    /// <c>Data Source=</c> are two such strings) fills a SECOND pool that clearing the first does
    /// not touch. Registering it here is what keeps the delete below from racing a handle the
    /// fixture does not know about; it is not redundant with <see cref="ConnectionString"/>.</para>
    /// </summary>
    public void AlsoClearPoolFor(string connectionString) =>
        _extraConnectionStrings.Add(connectionString);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // OUTSTANDING WORK MUST ALREADY BE JOINED BY THE TIME THIS RUNS. Clearing the pool and
        // deleting the file while a worker still holds a connection is exactly how an earlier
        // scoped-clearing attempt regressed Data ConcurrencyTests with file-in-use failures. Every
        // class that spawns background work over its fixture database joins it inside the test
        // itself today (ConcurrencyTests via Task.WaitAll/WhenAll, MaintenanceHostedServiceTests
        // via an awaited StopAsync), so this type deliberately offers no join hook of its own — an
        // unused one would be untested API pretending to be a safety net. A class that genuinely
        // cannot join in-test (arb-rga.4's parallel Integration work may produce one) should add
        // that hook back here, with a caller, rather than assume this ordering protects it.

        // 1. Clear ONLY this database's pool. Keyed by connection string, so a throwaway connection
        //    built from ours names exactly the pool EF filled — and no neighbour's.
        foreach (var connectionString in _extraConnectionStrings.Prepend(ConnectionString))
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }

        // 2. Now the file can go, along with the sidecars WAL mode leaves behind.
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = Path + suffix;

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Best-effort, matching the web application factories: a SQLite file still locked by
                // a host that has only just been disposed is a Windows-only nuisance and must never
                // fail an otherwise passing run. CI is Linux, where this does not arise.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning as IOException above.
            }
        }
    }
}
