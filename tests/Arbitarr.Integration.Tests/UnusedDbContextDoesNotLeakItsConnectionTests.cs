using Arbitarr.Data;
using Arbitarr.Data.Backup;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-auam: an <see cref="ArbitarrDbContext"/> that is RESOLVED and then disposed WITHOUT EVER
/// BEING USED must not leave its SQLite handle open.
///
/// <para><b>Why the unused case needs its own test rather than riding on
/// <see cref="ConfigDirectoryIsDeletedOnDisposalTests"/>.</b> That class drives a real request, so
/// every context it creates is used — and a used context is exactly the case that already worked.
/// EF's <c>RelationalConnection</c> ADOPTS its connection lazily, on first use, so while
/// <c>ArbitarrDbContextOptionsFactory.Create</c> opened the connection eagerly the ownership flag
/// (arb-dhua) had nothing to act on for a context nobody touched: the handle was never closed and
/// never RETURNED to the pool, so no <c>ClearPool</c> at any scope could reach it and it survived
/// for the life of the process. A test that uses its context cannot see that at all, which is
/// precisely why the residue outlived arb-dhua's fix and was misread as a property of particular
/// test classes.</para>
///
/// <para><b>This test is its own mutation control.</b> On the eager-open implementation it FAILS —
/// measured, and re-measured out of repo per CLAUDE.md §4 against both implementations side by side
/// (3/3 leaked eagerly, 0/3 lazily), with the used-context case clean under both, which is the
/// asymmetry that makes the unused path the subject here.</para>
///
/// <para><b>The production shape this stands in for</b> is a scope whose context is resolved on a
/// path that returns early — <c>Program.cs</c>, <c>MaintenanceHostedService</c> and
/// <c>DownloadRefusalRehydrationService</c> all do it. In a long-running host each such scope leaked
/// one connection permanently; the config directory is simply where that becomes assertable.</para>
/// </summary>
public sealed class UnusedDbContextDoesNotLeakItsConnectionTests : IAsyncLifetime
{
    /// <summary>
    /// THIS CLASS owns the directory, so it uses
    /// <see cref="ArbitarrWebApplicationFactory.OverConfigDirectory"/> (which deliberately does not
    /// delete) and deletes it itself. That is what lets the assertion be made against
    /// <see cref="ConfigDirectoryTeardown.Delete"/> explicitly, in the test body, rather than against
    /// a factory's swallowed <c>TryDelete</c> — a cleanup failure recorded and ignored is the
    /// "absence that was never detectable" shape arb-gphi removed.
    /// </summary>
    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-auam-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Nothing to set up: each test builds and owns its own host.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// A safety net only, and <see cref="IAsyncLifetime"/> rather than <c>IDisposable</c> or a bare
    /// <c>IAsyncDisposable</c> per docs/standards/process.md — xunit v2 honours only the former two,
    /// so teardown written as the latter silently never runs.
    ///
    /// <para>Each test already disposes its host and then deletes the directory as its own
    /// assertion, so by the time this runs the directory is normally gone.
    /// <see cref="ConfigDirectoryTeardown.TryDelete"/> rather than <c>Delete</c> precisely because
    /// this is the safety net: a failure here would be re-reporting the defect the test itself
    /// asserts on, and the assertion is the honest place for that failure to land.</para>
    /// </summary>
    public Task DisposeAsync()
    {
        ConfigDirectoryTeardown.TryDelete(_configDirectory);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_resolved_but_unused_DbContext_releases_its_handle_when_its_scope_is_disposed()
    {
        var databasePath = new BackupPaths(_configDirectory).DatabasePath;

        await using (var host = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory))
        {
            // Forces the host to build and migrate, so the database FILE exists and its pools are in
            // play. Without this the directory would be trivially deletable and the assertion below
            // would pass in the broken world too.
            using var client = host.CreateClient();
            await client.GetAsync("/api/status");

            using var scope = host.Services.CreateScope();

            // THE SUBJECT: resolved, and deliberately NEVER USED. No query, no SaveChanges, not even
            // a Database.GetDbConnection() touch — any one of those makes EF adopt the connection and
            // the leak disappears, which is what kept this invisible. The discard is the whole point
            // and must not be "tidied" into a use.
            _ = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        }

        // NON-VACUITY, asserted before the real check: the database must actually exist, or there was
        // no handle in play and a successful delete would say nothing about connection ownership.
        Assert.True(
            File.Exists(databasePath),
            "The host never created its database, so no SQLite handle was ever in play and this test " +
            "would pass without exercising the property it exists to pin.");

        // THE ASSERTION. Delete (not TryDelete) throws with the teardown's full diagnostic when a
        // handle survives — which on the eager-open implementation is exactly what happens.
        //
        // ITS OBSERVABLE IS WINDOWS SHARE-LOCK SEMANTICS, SO THIS TEST DOES NOT PIN THE PROPERTY ON
        // EVERY PLATFORM. A surviving handle blocks a directory delete on Windows; on Linux unlink
        // removes the name with the handle still open, so the delete SUCCEEDS with the leak fully in
        // place and this test would pass against the eager-open implementation on that shard —
        // proving nothing there. That is not a defect in this assertion, it is the platform: the
        // cross-platform property is carried by A_retained_connection_survives_the_same_teardown,
        // which asserts on the connection's STATE rather than on a delete, and by the arb-1z1l IL
        // scan. Do not "strengthen" this into a platform-conditional here — the split lives in the
        // control, where it is the subject rather than an aside.
        ConfigDirectoryTeardown.Delete(_configDirectory);
    }

    /// <summary>
    /// THE POSITIVE CONTROL for the test above: proof that a genuinely retained handle is one the
    /// teardown CANNOT release, so the success above is a real observation rather than a delete that
    /// had become impossible to fail.
    ///
    /// <para>Without this, "the delete succeeded" is compatible with the delete having stopped
    /// detecting anything — a teardown helper that silently dropped its pool clear would turn the
    /// test above green while measuring nothing.</para>
    ///
    /// <para><b>WHY THIS DOES NOT SIMPLY REQUIRE THE DELETE TO THROW, AND CI IS WHERE THAT MATTERS.</b>
    /// An open handle blocks a directory delete on WINDOWS, where it carries a share lock; on LINUX
    /// — where CI runs — it does not, because unlink removes the NAME while the handle keeps the
    /// inode alive, so the delete SUCCEEDS with the handle fully retained. This control was first
    /// written as an unconditional <c>Assert.Throws&lt;IOException&gt;</c> and went red on the Linux
    /// runner for exactly that reason: not because the property was absent, but because the
    /// MEASUREMENT does not exist on that platform. The same account is recorded on
    /// <see cref="ConfigDirectoryIsDeletedOnDisposalTests"/> and
    /// <see cref="ConfigDirectoryTeardown"/>'s own tests, which hit it before this class did.</para>
    ///
    /// <para><b>So the assertion is on what a retained handle does on BOTH platforms</b>, following
    /// <c>SqlitePoolCleanerTests</c>: the retained connection is still OPEN and still SERVING the
    /// database after the teardown has run its pool clear over it. That is the property that makes
    /// the test above meaningful — not "a delete fails", but "the teardown's clear cannot reach a
    /// handle nothing returned to the pool", which is precisely the defect shape arb-auam fixed. It
    /// is true on Windows and Linux alike and it is what a dropped clear or a handle that was never
    /// really open would break. Neither branch below is a skip, and neither platform is left
    /// unproven: the cross-platform observable is asserted unconditionally, and the Windows-only
    /// share-lock throw is asserted ADDITIONALLY where the platform makes it meaningful.</para>
    ///
    /// <para>The connection is opened through <see cref="SqliteConnectionFactory"/> rather than
    /// constructed inline, so it is the same connection shape and the same pool the assertion above
    /// is clearing — a handle from some other string would be a control for the wrong thing.</para>
    /// </summary>
    [Fact]
    public async Task A_retained_connection_survives_the_same_teardown()
    {
        var databasePath = new BackupPaths(_configDirectory).DatabasePath;

        await using (var host = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory))
        {
            using var client = host.CreateClient();
            await client.GetAsync("/api/status");
        }

        // NON-VACUITY: the same check the test above makes. Without a database there is no handle to
        // retain and everything below would be controlling nothing.
        Assert.True(
            File.Exists(databasePath),
            "The host never created its database, so no SQLite handle was ever in play and this " +
            "control would demonstrate nothing.");

        // THE PLANTED DEFECT: an open connection nothing will return to the pool — the exact shape a
        // resolved-but-unused DbContext left behind on the eager-open implementation. Deliberately
        // held across the teardown.
        using var retained = new SqliteConnectionFactory(
            new SqliteConnectionOptions { DatabasePath = databasePath }).OpenConnection();

        Assert.Equal(System.Data.ConnectionState.Open, retained.State);

        if (OperatingSystem.IsWindows())
        {
            // WINDOWS ONLY, and stated as such: the retained handle's share lock makes the delete
            // lose, so here the throw IS available as a second, stronger detection. Asserting it
            // where it is real keeps this platform's coverage at full strength rather than reducing
            // both platforms to the weaker common denominator.
            var failure = Assert.Throws<IOException>(() => ConfigDirectoryTeardown.Delete(_configDirectory));

            // The helper's own diagnostic, so a future refactor that changed what Delete throws on
            // cannot leave this asserting on an unrelated IOException.
            Assert.Contains(
                "could not delete its config directory", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            // POSIX: unlink does not need the file to be unused, so the delete succeeds with the
            // handle in place and there is no throw to require. Run the same teardown anyway — the
            // point is that it runs its pool clear over this database and STILL cannot release this
            // handle, which is asserted below on both platforms.
            ConfigDirectoryTeardown.Delete(_configDirectory);
        }

        // THE CROSS-PLATFORM ASSERTION, and the one that actually carries this control. The teardown
        // has now run its ClearPoolsFor over this database on either platform. A connection that was
        // never RETURNED to the pool is not among the connections a pool HOLDS, so the clear cannot
        // have touched it: it must still be open and still serving. If this ever goes quiet — the
        // handle closed, or never usable in the first place — the test above is no longer known to be
        // capable of failing, on Windows or Linux.
        Assert.Equal(System.Data.ConnectionState.Open, retained.State);

        using var command = retained.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema;";

        // The real schema, so this is a read of the host's database rather than of an empty file some
        // later open happened to create at the same path.
        Assert.True(
            Assert.IsType<long>(command.ExecuteScalar()) > 0,
            "The retained connection is open but its database has no schema, so it is not serving " +
            "the host's database and this control is planting the wrong defect.");
    }
}
