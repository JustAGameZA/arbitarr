using Arbitarr.Data.Backup;
using Arbitarr.TestSupport;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// Removes a per-instance <c>/config</c> directory built by a test host, closing the pooled SQLite
/// handles first so the delete can actually win.
///
/// <para><b>Why this is ONE helper rather than the block each caller used to carry (arb-gphi).</b>
/// The teardown has two halves that are useless apart, and for as long as it was copied per caller
/// most callers had only one of them or neither. Measured on a full Integration.Tests run: nine
/// classes built a config directory in their constructor and never deleted it at all, and a further
/// handful called <c>Directory.Delete</c> with no pool clear inside an empty <c>catch
/// (IOException)</c>, so the delete failed and said nothing — 83 directories leaked per run. Having
/// exactly one implementation is what stops the pairing being half-implemented a fourth time; a
/// caller can no longer get the clear and the delete out of step because it no longer writes
/// either.</para>
///
/// <para><b>WHY THE POOL CLEARS ARE HERE, AND WHY THEY ARE ONLY ONE THIRD OF WHAT IS NEEDED.</b>
/// Draining the host stops the WORK; these two calls close the pooled FILE HANDLES. But a clear
/// closes only the connections a pool HOLDS, so it depends on the other two halves of the hand-over
/// in <c>ArbitarrDbContextOptionsFactory.Create</c> — <c>contextOwnsConnection: true</c> (arb-dhua),
/// so a disposed context RETURNS its connection to the pool, and that connection being handed over
/// CLOSED (arb-auam), so EF owns it even when the context is never used. Conversely ownership alone
/// does not delete the directory either: disposal only RETURNS the handle to the pool, and a pooled
/// handle still holds a share lock on Windows — measured, not assumed. All three are required, and
/// each hides the others' absence. The full history, the two widenings of the pool-clear inventory
/// that failed before the cause was found, and the alternatives beaten are in
/// <c>docs/adr/0017-sqlite-connection-lifetime-for-ef-contexts.md</c>.</para>
///
/// <para><b>Never <c>SqliteConnection.ClearAllPools()</c> regardless</b>: the process-global form
/// force-closes pooled connections belonging to test classes running in parallel (arb-cbc/arb-5ba)
/// and is banned from test IL with no allow-list by
/// <c>Arbitarr.Architecture.Tests.TestProcessGlobalStateTests</c>.</para>
///
/// <para>The main database is named via <c>BackupPaths</c> rather than a literal
/// <c>"arbitarr.db"</c>: that type owns where the database lives, and a second spelling of the name
/// is exactly what CLAUDE.md §1 warns silently stops covering the file when it moves.</para>
/// </summary>
internal static class ConfigDirectoryTeardown
{
    /// <summary>
    /// The number of delete attempts. A retry loop is not a flake-hiding retry of the TEST: since
    /// arb-dhua and arb-auam the first attempt succeeds, and this exists only because a directory can
    /// still be momentarily locked by something outside the test's control (a virus scanner, an
    /// indexer). The final failure is not swallowed — see <see cref="Delete"/>.
    /// </summary>
    private const int Attempts = 10;

    /// <summary>
    /// Clears the pooled handles on the directory's databases and deletes it, THROWING if it cannot.
    ///
    /// <para><b>A failed delete must fail the test (CLAUDE.md §4).</b> Callers used to wrap this in
    /// an empty <c>catch (IOException)</c>, which is the "absence that was never detectable" shape:
    /// the cleanup stopped working and every run stayed green. Throwing is correct for a test class
    /// disposing its OWN directory, because the failure lands on the class that owns it.</para>
    ///
    /// <para>This is deliberately NOT what the two factories do. A <c>WebApplicationFactory</c> is
    /// disposed by xunit as a class fixture, where a throw would fault whichever unrelated test
    /// happened to be in flight rather than the one that owns the factory — so they call
    /// <see cref="TryDelete"/> and record the failure for
    /// <c>ConfigDirectoryIsDeletedOnDisposalTests</c> to assert on instead.</para>
    /// </summary>
    public static void Delete(string configDirectory)
    {
        var failure = TryDelete(configDirectory);

        if (failure is not null)
        {
            throw new IOException(
                $"Test cleanup could not delete its config directory after {Attempts} attempts. " +
                "Every half of the teardown is required: the pool clear (here), EF being given " +
                "ownership of the connection it is handed (ArbitarrDbContextOptionsFactory.Create, " +
                "contextOwnsConnection: true), and that connection being handed over CLOSED so EF " +
                "owns it even when the context is never used (CreateUnopenedConnection, arb-auam). " +
                "Check all three before assuming an external lock holder.\n" +
                $"  Recorded failure: {failure.GetType().Name}: {failure.Message}",
                failure);
        }
    }

    /// <summary>
    /// Closes the pooled handles on the directory's databases WITHOUT deleting it.
    ///
    /// <para>Separated from <see cref="TryDelete"/> for the caller-supplied-directory host added by
    /// arb-v3w (<c>ArbitarrWebApplicationFactory.OverConfigDirectory</c>), where the two halves come
    /// apart for the only good reason they ever do: a SECOND host must open the same database after
    /// the first is disposed, so the delete is skipped while the clear is exactly what makes the
    /// restart possible. Releasing the handles is what lets the next host — or the caller's own
    /// cleanup — open the file at all.</para>
    ///
    /// <para>This is the one split that is legitimate, and it is not the half-implemented pairing
    /// arb-gphi removed: there, callers deleted WITHOUT clearing, which silently could not work.
    /// Clearing without deleting is a complete operation with a caller who deletes later.</para>
    /// </summary>
    public static void ClearPools(string configDirectory)
    {
        SqlitePoolCleaner.ClearPoolsFor(new BackupPaths(configDirectory).DatabasePath);

        // The log store is a SECOND database (arbitarr-logs.db) with its own connection shape, and it
        // sits under the same directory — so clearing only the main one leaves a delete losing to a
        // log handle instead. ClearPoolsForDirectory covers it and anything a test restored beside
        // them.
        SqlitePools.ClearPoolsForDirectory(configDirectory);
    }

    /// <summary>
    /// The same teardown, returning the final failure instead of throwing, for the two factories —
    /// see the remarks on <see cref="Delete"/> for why they must not throw from disposal.
    /// </summary>
    /// <returns>The exception from the FINAL attempt, or null when the directory is gone.</returns>
    public static Exception? TryDelete(string configDirectory)
    {
        ClearPools(configDirectory);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(configDirectory))
                {
                    return null;
                }

                Directory.Delete(configDirectory, recursive: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UnauthorizedAccessException as well as IOException: Windows raises that one for a
                // file another handle still has open, and catching only IOException let it escape.
                if (attempt == Attempts)
                {
                    return ex;
                }

                Thread.Sleep(100);
            }
        }

        return null;
    }
}
