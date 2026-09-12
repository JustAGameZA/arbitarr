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
        ConfigDirectoryTeardown.Delete(_configDirectory);
    }

    /// <summary>
    /// THE POSITIVE CONTROL for the test above: proof that a genuinely retained handle WOULD be
    /// caught by that assertion.
    ///
    /// <para>Without this, "the delete succeeded" is compatible with the delete having become
    /// impossible to fail — a teardown helper that silently stopped clearing pools, or a platform
    /// that no longer holds a share lock, would turn the test above green while detecting nothing.
    /// Here an open connection on the same database is held ACROSS the delete, so the delete must
    /// fail; that it does is what makes the success above meaningful.</para>
    ///
    /// <para>The connection is opened through <see cref="SqliteConnectionFactory"/> rather than
    /// constructed inline, so it is the same connection shape and the same pool the assertion above
    /// is clearing — a handle from some other string would be a control for the wrong thing.</para>
    /// </summary>
    [Fact]
    public async Task A_retained_connection_makes_the_same_delete_fail()
    {
        var databasePath = new BackupPaths(_configDirectory).DatabasePath;

        await using (var host = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory))
        {
            using var client = host.CreateClient();
            await client.GetAsync("/api/status");
        }

        using var retained = new SqliteConnectionFactory(
            new SqliteConnectionOptions { DatabasePath = databasePath }).OpenConnection();

        var failure = Assert.Throws<IOException>(() => ConfigDirectoryTeardown.Delete(_configDirectory));

        // The helper's own diagnostic, so a future refactor that changed what Delete throws on cannot
        // leave this asserting on an unrelated IOException.
        Assert.Contains("could not delete its config directory", failure.Message, StringComparison.Ordinal);
    }
}
