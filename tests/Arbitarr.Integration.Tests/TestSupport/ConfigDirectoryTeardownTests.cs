using Arbitarr.Data;
using Arbitarr.Data.Backup;
using Xunit;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// arb-gphi: pins that <see cref="ConfigDirectoryTeardown"/> actually removes a directory whose
/// database has been through the connection pool, and that a failure is REPORTED rather than
/// swallowed.
///
/// <para><b>Why the helper needs its own tests at all.</b> Every caller of it now relies on it as
/// their only cleanup. Before arb-gphi the teardown was copied per caller and most copies had one
/// half of it or neither, which is the failure mode centralising it removes — but only while the one
/// remaining implementation is itself pinned. An untested helper that silently stopped clearing
/// pools would put all ~16 callers back where they started, green the whole way.</para>
/// </summary>
public sealed class ConfigDirectoryTeardownTests
{
    /// <summary>
    /// Builds a directory holding a real database opened through the production connection factory,
    /// then CLOSED — so the handle has been returned to the pool and the pool is what holds it. That
    /// is the state a disposed test host leaves behind since arb-dhua, and the state the pool clear
    /// exists to resolve.
    /// </summary>
    private static string NewDirectoryWithAPooledDatabase()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "arbitarr-gphi-teardown-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        _ = SeedPooledDatabase(directory);

        return directory;
    }

    /// <summary>
    /// Creates a real database in <paramref name="directory"/> through the production connection
    /// factory and CLOSES it, leaving the handle in the pool, and returns its path.
    ///
    /// <para>Shared by the top-level and nested cases so the two cannot differ in HOW the database
    /// came to be pooled — the nested test's whole claim is that only the LOCATION differs, and a
    /// second copy of this setup would quietly undermine that.</para>
    /// </summary>
    private static string SeedPooledDatabase(string directory)
    {
        var databasePath = new BackupPaths(directory).DatabasePath;

        var connectionFactory = new SqliteConnectionFactory(
            new SqliteConnectionOptions { DatabasePath = databasePath });

        // arb-itmm: since the WAL work OpenConnection only VERIFIES the journal mode and refuses a
        // database still in 'delete' mode, so the one-time conversion Program.cs runs at startup has
        // to run here too — this helper opens the database without a host. It is not incidental
        // setup: without it every test in this class fails before reaching its assertion.
        connectionFactory.ConvertToWalOnce();

        using (var connection = connectionFactory.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        // NON-VACUITY: the database must exist, or the pool would hold nothing and a delete would
        // succeed for reasons that have nothing to do with the helper's clear.
        Assert.True(File.Exists(databasePath), "The probe database was never created.");

        return databasePath;
    }

    /// <summary>
    /// THE PROPERTY: the helper removes a directory whose database has a pooled connection against
    /// it. On Windows this is the whole point — the pooled handle holds a share lock and a bare
    /// <c>Directory.Delete</c> loses to it (see the negative control below).
    /// </summary>
    [Fact]
    public void Delete_removes_a_directory_whose_database_has_a_pooled_connection()
    {
        var directory = NewDirectoryWithAPooledDatabase();

        ConfigDirectoryTeardown.Delete(directory);

        Assert.False(Directory.Exists(directory), $"The helper left the directory behind: {directory}");
    }

    /// <summary>
    /// arb-j4hq, THE PROPERTY: the helper removes a directory whose database sits in a SUBDIRECTORY,
    /// not directly under the path it is handed.
    ///
    /// <para><b>Why this needed its own test rather than being covered by the one above.</b> The
    /// helper runs two clears that look redundant and are not.
    /// <c>SqlitePoolCleaner.ClearPoolsFor</c> walks the exact connection strings the application
    /// opens <c>arbitarr.db</c> with — the only set naming the pools that actually hold its handles —
    /// and used to be applied to the handed-in path ALONE.
    /// <c>SqlitePools.ClearPoolsForDirectory</c> recurses, but clears only the bare
    /// <c>Data Source=</c> pool per file, which is a different pool key and is EMPTY for a connection
    /// the application opened. So a nested database had its real pools cleared by neither: the
    /// recursive call found the file and cleared a pool nobody had filled.</para>
    ///
    /// <para><b>Measured, and it is why per-host config subdirectories were possible at all.</b>
    /// Giving each host in four test classes its own subdirectory turned eleven passing tests into
    /// "The process cannot access the file 'arbitarr.db' because it is being used by another
    /// process" — every one naming the MAIN database and never the log one, exactly the asymmetry
    /// the two helpers predict. The test above cannot catch that: its database is at the top level,
    /// where the full-string clear always reached.</para>
    /// </summary>
    [Fact]
    public void Delete_removes_a_directory_whose_pooled_database_is_in_a_subdirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "arbitarr-j4hq-teardown-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var nested = PerHostConfigDirectory.Create(root);
            var databasePath = SeedPooledDatabase(nested);

            // NON-VACUITY: the database really is nested, so this test cannot pass by accidentally
            // exercising the top-level case the test above already covers.
            Assert.Equal(nested, Path.GetDirectoryName(databasePath));
            Assert.NotEqual(root, nested);

            ConfigDirectoryTeardown.Delete(root);

            Assert.False(Directory.Exists(root), $"The helper left the directory behind: {root}");
        }
        finally
        {
            ConfigDirectoryTeardown.TryDelete(root);
        }
    }

    /// <summary>
    /// THE NEGATIVE CONTROL for the test above (CLAUDE.md §4). "The directory is gone" passes just as
    /// happily if nothing was ever holding it, so this demonstrates that a nested pooled database
    /// DOES block a bare delete — the state the helper has to win against.
    ///
    /// <para>Windows-only for the same platform reason as the control below, and asserting the POSIX
    /// behaviour rather than skipping there.</para>
    /// </summary>
    [Fact]
    public void Without_the_pool_clear_a_bare_delete_of_a_nested_pooled_database_fails_on_windows()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "arbitarr-j4hq-teardown-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            _ = SeedPooledDatabase(PerHostConfigDirectory.Create(root));

            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsAny<IOException>(() => Directory.Delete(root, recursive: true));
            }
            else
            {
                Directory.Delete(root, recursive: true);
                Assert.False(Directory.Exists(root));
            }
        }
        finally
        {
            ConfigDirectoryTeardown.TryDelete(root);
        }
    }

    /// <summary>
    /// THE NEGATIVE CONTROL (CLAUDE.md §4). "The directory is gone" passes just as happily if the
    /// pool clear did nothing and the delete would have succeeded anyway — which is exactly what a
    /// future edit dropping <c>ClearPoolsFor</c> would produce. So this demonstrates the OTHER half:
    /// with the same pooled state but WITHOUT the clear, a bare delete fails.
    ///
    /// <para><b>This control is Windows-only, and that is a platform fact rather than a skip.</b> A
    /// pooled SQLite handle blocks a directory delete on Windows, where it carries a share lock; on
    /// POSIX it does not — unlink removes the name while the handle keeps the inode alive, so a bare
    /// delete SUCCEEDS with the pool fully populated. Requiring a throw everywhere is precisely how
    /// arb-dhua's own control first went red on the Linux CI runner
    /// (<see cref="ConfigDirectoryIsDeletedOnDisposalTests"/> carries that account). Asserting a
    /// throw on Linux would therefore be asserting something false.</para>
    ///
    /// <para>The consequence worth stating plainly, as that class also states: on Linux the test
    /// above cannot detect a dropped pool clear, because the delete succeeds either way. The
    /// cross-platform guard is not this file — it is that there is now ONE implementation, so a
    /// dropped clear is a one-line diff in one reviewed place rather than a silent divergence across
    /// sixteen copies.</para>
    /// </summary>
    [Fact]
    public void Without_the_pool_clear_a_bare_delete_fails_on_windows()
    {
        var directory = NewDirectoryWithAPooledDatabase();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // THE CONTROL: the pooled handle wins, so the clear the helper performs is doing
                // real work rather than being decorative. If this ever stops throwing, the test
                // above has stopped proving anything on this platform.
                Assert.ThrowsAny<IOException>(() => Directory.Delete(directory, recursive: true));
            }
            else
            {
                // POSIX: the bare delete succeeds with the pool populated, so there is no throw to
                // require. Assert that rather than skipping, so this branch still says something
                // true about the platform it runs on.
                Directory.Delete(directory, recursive: true);
                Assert.False(Directory.Exists(directory));
            }
        }
        finally
        {
            // Whatever the platform did, leave nothing behind — this file must not become a
            // contributor to the leak it exists to prevent.
            ConfigDirectoryTeardown.TryDelete(directory);
        }
    }

    /// <summary>
    /// arb-s3ky, THE PROPERTY: a pooled connection OPENED AND RELEASED after the teardown's first
    /// pool clear is still beaten, because the clear runs on EVERY attempt rather than once before
    /// the loop.
    ///
    /// <para><b>MUTANT KILLED: moving <c>ClearPools</c> back out of the loop.</b> That is the code as
    /// it stood, and the shape this test forbids: a single pre-loop clear cannot close a handle that
    /// enters the pool afterwards, so the ten attempts that follow all lose to the same lock and the
    /// delete fails. Proved by running this test against that exact one-line change — it fails there
    /// and passes here. This is the mechanism the bead recorded observing: the log pump's bounded
    /// shutdown returns, its final drain opens a pooled connection a moment later, and that handle
    /// lands in a pool the teardown had already done its only clear on.</para>
    ///
    /// <para><b>Why the connection must be OPENED after the clear, not merely RELEASED after it</b> —
    /// which is the version of this test that does NOT bite, and the distinction is easy to get
    /// wrong. <c>SqliteConnection.ClearPool</c> bumps the pool's generation, so a connection that was
    /// already CHECKED OUT when the clear ran is CLOSED when it is released rather than returned to
    /// the pool. Measured, not assumed. A test that opens a handle, lets the teardown clear, then
    /// releases it therefore leaves nothing pooled and the delete succeeds with or without the
    /// per-attempt clear — it passes against the mutant, which is to say it tests nothing. Only a
    /// connection whose whole open-and-release cycle happens AFTER a clear ends up pooled where no
    /// earlier clear could have reached it.</para>
    ///
    /// <para>Windows-only for the same platform reason as the control above: on POSIX a pooled handle
    /// does not block a delete at all, so there is nothing for a second clear to win. The POSIX
    /// branch asserts that instead of skipping.</para>
    /// </summary>
    [Fact]
    public async Task Delete_wins_against_a_connection_pooled_after_the_first_attempt()
    {
        var directory = NewDirectoryWithAPooledDatabase();
        var databasePath = new BackupPaths(directory).DatabasePath;

        if (!OperatingSystem.IsWindows())
        {
            // On POSIX the pooled handle never blocks the delete, so the per-attempt clear has
            // nothing to demonstrate. Assert the delete succeeds so this branch still says something
            // true about the platform it runs on.
            ConfigDirectoryTeardown.Delete(directory);
            Assert.False(Directory.Exists(directory));
            return;
        }

        var connectionFactory = new SqliteConnectionFactory(
            new SqliteConnectionOptions { DatabasePath = databasePath });

        // OPEN across the loop's first attempts, so those attempts FAIL and the loop is still running
        // when the reopen below lands. Without this the very first attempt succeeds — measured — and
        // there is no window inside the loop for anything to land in.
        var blocker = connectionFactory.OpenConnection();

        using var deleteEntered = new ManualResetEventSlim(false);
        var reopened = 0;

        try
        {
            var deleteTask = Task.Run(() =>
            {
                deleteEntered.Set();
                return ConfigDirectoryTeardown.TryDelete(directory);
            });

            Assert.True(
                deleteEntered.Wait(TimeSpan.FromSeconds(30)),
                "The delete never started, so there was no loop for the reopen to land inside.");

            // The loop sleeps 100ms between its ten attempts, so this lands after the first two or
            // three have failed against the blocker and well before the tenth.
            await Task.Delay(250);

            // THE HANDLE THE PROPERTY IS ABOUT: a full open-and-release cycle occurring after the
            // teardown's first clear, which therefore leaves a connection in the pool that no
            // earlier clear could have reached. This is the log pump's final drain in miniature.
            // (The blocker is dropped first so the delete's remaining attempts are contending only
            // with this newly pooled handle.)
            blocker.Dispose();
            connectionFactory.OpenConnection().Dispose();
            Interlocked.Exchange(ref reopened, 1);

            var failure = await deleteTask;

            Assert.Null(failure);
            Assert.False(
                Directory.Exists(directory),
                "The delete lost to a connection pooled after the first attempt. That is only " +
                "winnable if ClearPools runs on every attempt — a single clear before the loop " +
                "cannot close a handle that entered the pool after it ran.");

            // NON-VACUITY: the reopen really happened, so the delete above was won against a pooled
            // handle rather than against an empty pool.
            Assert.Equal(1, Volatile.Read(ref reopened));
        }
        finally
        {
            blocker.Dispose();
            ConfigDirectoryTeardown.TryDelete(directory);
        }
    }

    /// <summary>
    /// arb-s3ky: the awaiting entry point WAITS for an incomplete drain completion before touching
    /// the directory, and proceeds once it completes.
    ///
    /// <para><b>MUTANT KILLED:</b> ignoring <c>drainCompletions</c> (or waiting on it after the
    /// delete). The directory would then be gone before the completion was signalled, which the
    /// ordering assertion below catches.</para>
    /// </summary>
    [Fact]
    public async Task The_teardown_waits_for_an_incomplete_drain_completion_before_deleting()
    {
        var directory = NewDirectoryWithAPooledDatabase();

        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deleteStarted = new ManualResetEventSlim(false);

        var deleteTask = Task.Run(() =>
        {
            deleteStarted.Set();
            ConfigDirectoryTeardown.Delete(directory, [drain.Task]);
        });

        Assert.True(deleteStarted.Wait(TimeSpan.FromSeconds(30)));

        // While the completion is outstanding the teardown must not have deleted anything. The pause
        // gives a non-waiting implementation ample time to finish, so this is evidence rather than a
        // race the correct implementation happens to win.
        await Task.Delay(500);
        Assert.True(
            Directory.Exists(directory),
            "The teardown deleted the directory while a drain completion was still outstanding, so " +
            "it is not waiting for it — the whole point of the entry point being given one.");
        Assert.False(deleteTask.IsCompleted);

        drain.SetResult();

        await deleteTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// arb-s3ky: the wait GIVES UP at its bound rather than hanging the run. A pump that never
    /// finishes must surface as the delete failure the caller already reports, not as a test process
    /// that never exits.
    ///
    /// <para>The bound is passed in as a parameter — which is exactly why
    /// <c>TryDelete</c> takes one. A 30-second constant inlined in the helper would leave this
    /// property either untested or a 30-second test; a bound a test cannot reach is a bound nothing
    /// pins.</para>
    /// </summary>
    [Fact]
    public void The_teardown_gives_up_on_a_drain_completion_that_never_completes()
    {
        var directory = NewDirectoryWithAPooledDatabase();

        // Never completed, and never will be.
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var failure = ConfigDirectoryTeardown.TryDelete(
                directory, [neverCompletes.Task], TimeSpan.FromMilliseconds(200));
            elapsed.Stop();

            // It RETURNED — the property. Had it awaited unboundedly this line would never run and
            // the test would hang rather than fail, which is why the bound matters.
            Assert.False(Directory.Exists(directory));
            Assert.Null(failure);

            // ... and it gave up near its bound rather than running to the 30s default. Loose on
            // purpose: this guards against the bound being ignored, not against a slow machine.
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(15),
                $"The teardown took {elapsed.Elapsed} against a 200ms bound, so the bound it was " +
                "given is not the one it used.");

            // NON-VACUITY: the task really never completed, so the return above was the give-up path
            // and not the completion path arriving early.
            Assert.False(neverCompletes.Task.IsCompleted);
        }
        finally
        {
            ConfigDirectoryTeardown.TryDelete(directory);
        }
    }

    /// <summary>
    /// A failed delete must FAIL the caller, not be swallowed. This is the half that distinguishes
    /// the helper from the empty <c>catch (IOException)</c> blocks it replaced: those made a broken
    /// teardown indistinguishable from a working one, which is how 83 directories a run went
    /// unnoticed (CLAUDE.md §4).
    ///
    /// <para>Windows-only for the same reason as the control above: it needs a delete that genuinely
    /// cannot succeed, and only Windows' share lock provides one. The planted handle is opened and
    /// deliberately NOT closed, so no pool clear can reach it — the same shape as the un-owned EF
    /// connection in arb-dhua.</para>
    /// </summary>
    [Fact]
    public void A_delete_that_cannot_succeed_throws_rather_than_being_swallowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            // On POSIX the delete succeeds even with the handle open, so there is no unsatisfiable
            // delete to construct. Assert THAT, so this test is not a silent no-op here.
            var posixDirectory = NewDirectoryWithAPooledDatabase();
            ConfigDirectoryTeardown.Delete(posixDirectory);
            Assert.False(Directory.Exists(posixDirectory));
            return;
        }

        var directory = NewDirectoryWithAPooledDatabase();
        var databasePath = new BackupPaths(directory).DatabasePath;

        // Held open on purpose: a connection nothing returns to the pool is one no clear can close.
        var held = new SqliteConnectionFactory(
            new SqliteConnectionOptions { DatabasePath = databasePath }).OpenConnection();

        try
        {
            var thrown = Assert.ThrowsAny<IOException>(() => ConfigDirectoryTeardown.Delete(directory));

            // The message must name every mechanism the teardown depends on, because that is the
            // diagnostic a future regression needs — a bare "access denied" sends the reader looking
            // for a virus scanner instead of a dropped ClearPoolsFor, a missing
            // contextOwnsConnection, or a connection handed to EF already open.
            //
            // Asserted per MECHANISM rather than against one summary phrase ("Both halves"), which is
            // what this line used to do: that phrasing silently stopped covering the third mechanism
            // the moment arb-auam added one, while still passing. Naming them individually means a
            // message that drops one fails here.
            Assert.Contains("pool clear", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("contextOwnsConnection: true", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("CreateUnopenedConnection", thrown.Message, StringComparison.Ordinal);
            Assert.NotNull(thrown.InnerException);
        }
        finally
        {
            held.Dispose();
            ConfigDirectoryTeardown.TryDelete(directory);
        }
    }
}
