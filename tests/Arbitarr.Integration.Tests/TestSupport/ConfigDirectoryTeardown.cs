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
    /// How long the teardown waits for a logger provider's published drain completion before
    /// proceeding anyway (arb-s3ky).
    ///
    /// <para><b>Why this is 30s while production's own shutdown bound is 5s
    /// (<c>SqliteLoggerProvider.DefaultShutdownWait</c>), and why the difference is the point rather
    /// than an inconsistency.</b> The two bounds answer different questions. Production's bounds a
    /// CONTAINER STOPPING: past a few seconds an operator would rather lose the last log lines than
    /// have a service that will not exit, so it gives up early on purpose. This one bounds a TEST
    /// PROCESS that has nothing else to do but wait, where giving up early is what produces the
    /// intermittent — a drain still in flight reopens the pooled handle the clear below just closed.
    /// Generous enough that a drain contending with four other test lanes finishes inside it;
    /// bounded at all only so a genuinely wedged pump fails the run with a delete error rather than
    /// hanging it forever.</para>
    ///
    /// <para>Note this is NOT a retry of a test and does not lengthen a passing run: the completion
    /// is normally already signalled by the time the host's disposal returns, so the wait costs
    /// nothing except in exactly the case it exists for.</para>
    /// </summary>
    public static readonly TimeSpan DrainCompletionWait = TimeSpan.FromSeconds(30);

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
    ///
    /// <para><b>arb-s3ky: <paramref name="drainCompletions"/> is WAITED FOR first, and a clear cannot
    /// replace it.</b> The pool-clear inventory was never the gap — <see cref="ClearPools"/> names
    /// <c>arbitarr-logs.db</c> and always has. The gap is TIMING.
    /// <c>SqliteLoggerProvider.Dispose</c> waits a bounded
    /// <c>SqliteLoggerProvider.DefaultShutdownWait</c> for its background pump and then GIVES UP, on
    /// purpose, so a wedged writer cannot hang a shutting-down container. Under a multi-lane run that
    /// bound can elapse while the pump's final write transaction is still open, so host disposal
    /// returns and this teardown runs against a log database a live drain is still using. A clear
    /// closes what a pool HOLDS, so it cannot reach a connection the pump OPENS after it ran — and a
    /// still-running pump opens one per flush. Awaiting the provider's own published completion is
    /// the only thing that establishes the pump is FINISHED rather than merely asked to stop. It
    /// stays OPTIONAL because callers that build a config directory without a host have no log pump
    /// to wait for.</para>
    /// </summary>
    /// <param name="configDirectory">The directory to remove.</param>
    /// <param name="drainCompletions">
    /// <c>SqliteLoggerProvider.DrainCompleted</c> for every provider that wrote into this directory,
    /// or null for a caller that has none.
    /// </param>
    public static void Delete(string configDirectory, IEnumerable<Task>? drainCompletions = null)
    {
        var failure = TryDelete(configDirectory, drainCompletions, DrainCompletionWait);

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
    /// <param name="configDirectory">The directory to remove.</param>
    /// <param name="drainCompletions">
    /// arb-s3ky: <c>SqliteLoggerProvider.DrainCompleted</c> for every provider that wrote into this
    /// directory, waited for BEFORE the first delete attempt, or null for a caller that has none.
    /// See <see cref="Delete"/> for why a pool clear cannot replace that wait. Those tasks never
    /// fault (the provider publishes from a <c>finally</c>), so this never throws on their behalf.
    /// </param>
    /// <param name="drainCompletionWait">
    /// The bound on that wait, defaulting to <see cref="DrainCompletionWait"/>. A PARAMETER rather
    /// than the constant inlined, so <see cref="ConfigDirectoryTeardownTests"/> can prove the give-up
    /// path in milliseconds instead of needing a 30-second test — a bound a test cannot reach is a
    /// bound nothing pins.
    /// </param>
    /// <returns>The exception from the FINAL attempt, or null when the directory is gone.</returns>
    public static Exception? TryDelete(
        string configDirectory,
        IEnumerable<Task>? drainCompletions = null,
        TimeSpan? drainCompletionWait = null)
    {
        WaitForDrains(drainCompletions, drainCompletionWait ?? DrainCompletionWait);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                // arb-s3ky: RE-CLEARED ON EVERY ATTEMPT, not once before the loop. The reasoning on
                // Attempts above — that this loop is not a flake-hiding retry of the TEST — extends
                // to the re-clear, and in fact is what forces it. The loop exists for a lock held by
                // something outside the test's control, and the SQLite log pump is precisely such a
                // holder: its bounded shutdown can return while a final drain is still in flight, so
                // a connection the pump OPENS after a single pre-loop clear lands in a pool no later
                // attempt would ever clear, and every one of them then loses to it. (Note the clear
                // DOES cover a connection that was merely checked out when it ran: clearing bumps
                // the pool generation, so that one is closed on release rather than pooled —
                // measured. It is the newly opened handle, not the released one, that needs this.)
                // Clearing once made the loop structurally unable to succeed in the case it was
                // there for; clearing per attempt makes each attempt a fresh, complete teardown
                // rather than a bare repeat of the half that already failed. The wait above is the
                // primary fix — this is what covers a handle the caller could not name a completion
                // for.
                ClearPools(configDirectory);

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

    /// <summary>
    /// Waits for every published drain completion, bounded by <paramref name="wait"/>.
    ///
    /// <para>Times out rather than throwing: a pump that never finishes should surface as the delete
    /// failure the caller already reports (which names the directory and the locking file), not as a
    /// second, less informative exception thrown before the delete was ever attempted. The teardown
    /// then proceeds and the per-attempt <see cref="ClearPools"/> in <see cref="TryDelete"/> gets its
    /// chances anyway.</para>
    ///
    /// <para>Synchronous by necessity: the callers are <c>Dispose</c> paths, including the
    /// <c>Dispose(bool)</c> override xunit uses for every class fixture, which cannot await.</para>
    /// </summary>
    private static void WaitForDrains(IEnumerable<Task>? drainCompletions, TimeSpan wait)
    {
        if (drainCompletions is null)
        {
            return;
        }

        var pending = drainCompletions.Where(task => task is not null).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            Task.WaitAll(pending, wait);
        }
        catch (AggregateException)
        {
            // DrainCompleted is published from a finally and so never faults; this is belt-and-braces
            // so a teardown cannot throw on a waited task's behalf from inside a Dispose.
        }
    }
}
