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

        return directory;
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
