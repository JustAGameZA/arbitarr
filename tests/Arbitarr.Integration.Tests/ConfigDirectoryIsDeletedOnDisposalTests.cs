using System.Net;
using Arbitarr.Data;
using Arbitarr.Data.Backup;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-dhua: disposing a test host must actually REMOVE its per-instance config directory.
///
/// <para><b>What was wrong, and why it was not what it looked like.</b> These factories had never
/// once deleted their config directories — ~12,354 leaked under <c>%TEMP%/arbitarr-m2-tests</c> and
/// ~9,814 under <c>arbitarr-remote-address-tests</c> when it was finally measured — and the delete
/// always failed with <c>IOException</c> on <c>arbitarr.db</c>. The natural reading was that the
/// pool clear was naming the wrong connection strings, and three separate attempts went into
/// widening that inventory. All three failed, because the inventory was never the problem.</para>
///
/// <para><b>The actual cause was OWNERSHIP.</b> <c>ArbitarrDbContextOptionsFactory.Create</c> opens
/// a connection eagerly and passed it to <c>UseSqlite(connection)</c> — an overload whose default
/// leaves ownership with the CALLER. Disposing the <c>ArbitarrDbContext</c> therefore did not
/// dispose the connection, so it was never RETURNED to the pool. <c>ClearPool</c> closes the
/// connections a pool HOLDS; one that never came back is not among them, so no clear at any scope
/// could ever have reached it. The fix is <c>contextOwnsConnection: true</c>, in production code —
/// a long-running host leaked a connection per scope for exactly the same reason.</para>
///
/// <para><b>Both halves are required</b>, which is measurable in isolation: with ownership
/// transferred but no pool clear the delete still fails, because disposal only returns the handle
/// to the pool and a pooled handle holds a share lock on Windows. The factories' existing clear is
/// the other half.</para>
///
/// <para><b>Why this is a separate class from <see cref="HostDisposalDrainsBackgroundWorkTests"/>.</b>
/// That one asserts the directory is QUIESCENT after disposal (arb-rwhb: background work drained).
/// This one asserts it is GONE. They are different properties with different causes, and keeping
/// them apart is what makes a regression in either attributable to the change that caused it —
/// which is precisely what went wrong before, when a deletion assertion would have been red for
/// arb-dhua's reason while claiming to say something about arb-rwhb's.</para>
/// </summary>
public sealed class ConfigDirectoryIsDeletedOnDisposalTests
{
    [Fact]
    public async Task The_asynchronous_disposal_path_deletes_the_config_directory()
    {
        var factory = new ArbitarrWebApplicationFactory();
        var configDirectory = factory.ConfigDirectory;

        using (var client = factory.CreateClient())
        {
            await client.GetAsync("/api/status");
        }

        // NON-VACUITY: the host must actually have opened the database, or there would be no handle
        // to release and this would pass in the broken world too.
        Assert.True(
            File.Exists(new BackupPaths(configDirectory).DatabasePath),
            "The host never created its database, so no connection handle was ever in play and this " +
            "test would pass without exercising the property it exists to pin.");

        await factory.DisposeAsync();

        AssertDirectoryWasDeleted(factory.LastDeleteFailure, configDirectory);
    }

    /// <summary>
    /// The synchronous path is pinned separately for the same reason arb-rwhb pins it separately:
    /// both shapes are in live use (xunit disposes the ~29 injected class fixtures synchronously,
    /// while several classes use <c>await using</c>), and <c>base.Dispose</c> cannot await hosted
    /// services the way <c>base.DisposeAsync</c> does. A fix verified on only one path can leave the
    /// other broken.
    /// </summary>
    [Fact]
    public async Task The_synchronous_disposal_path_deletes_the_config_directory()
    {
        var factory = new ArbitarrWebApplicationFactory();
        var configDirectory = factory.ConfigDirectory;

        using (var client = factory.CreateClient())
        {
            await client.GetAsync("/api/status");
        }

        Assert.True(
            File.Exists(new BackupPaths(configDirectory).DatabasePath),
            "The host never created its database, so this test never put a handle in play.");

        factory.Dispose();

        AssertDirectoryWasDeleted(factory.LastDeleteFailure, configDirectory);
    }

    /// <summary>
    /// <see cref="RemoteAddressWebApplicationFactory"/> hosts the same composition root and carries
    /// a byte-identical copy of the disposal logic. It is asserted here rather than assumed to
    /// follow, because the two files have drifted before — that duplication is exactly why both
    /// carry a KEEP IN STEP note.
    /// </summary>
    [Fact]
    public async Task The_remote_address_factory_also_deletes_its_config_directory()
    {
        // TEST-NET-1 (RFC 5737): a documentation address, never a real host.
        var factory = new RemoteAddressWebApplicationFactory(IPAddress.Parse("192.0.2.10"));
        var configDirectory = factory.ConfigDirectory;

        using (var client = factory.CreateClient())
        {
            await client.GetAsync("/api/status");
        }

        Assert.True(
            File.Exists(new BackupPaths(configDirectory).DatabasePath),
            "The host never created its database, so this test never put a handle in play.");

        await factory.DisposeAsync();

        AssertDirectoryWasDeleted(factory.LastDeleteFailure, configDirectory);
    }

    /// <summary>
    /// POSITIVE CONTROL (CLAUDE.md §4). An assertion that a directory is GONE is the classic
    /// vacuous shape: it would pass just as happily if the directory had never been created, if the
    /// host had never opened the database, or if <see cref="AssertDirectoryWasDeleted"/> checked
    /// nothing that could fail. Asserting the fixture exists first (as the three tests above do)
    /// proves the directory EXISTED; it does not prove the assertion would BITE if the leak came
    /// back.
    ///
    /// <para>So this plants the defect's own mechanism — an open, unreturned connection on the
    /// host's database, which is precisely what <c>UseSqlite(connection)</c> without ownership left
    /// behind — and requires the assertion to FAIL. Only once it is shown to fail does its silence
    /// in the three tests above carry information.</para>
    ///
    /// <para>The connection is opened through <see cref="Arbitarr.Data.SqliteConnectionFactory"/> with the
    /// production connection string, not a hand-built one: pools are keyed by the full string, so a
    /// plainer string would name a pool the factory's clear does not touch and the control would be
    /// planting a DIFFERENT defect from the one that shipped.</para>
    /// </summary>
    [Fact]
    public async Task The_deletion_assertion_fails_when_an_open_connection_is_planted()
    {
        var factory = new ArbitarrWebApplicationFactory();
        var configDirectory = factory.ConfigDirectory;

        using (var client = factory.CreateClient())
        {
            await client.GetAsync("/api/status");
        }

        var databasePath = new BackupPaths(configDirectory).DatabasePath;
        Assert.True(File.Exists(databasePath), "The host never created its database.");

        // The planted leak: an open connection nothing will return to the pool, which is what the
        // un-owned EF connection amounted to. Deliberately NOT disposed before disposal runs.
        var leaked = new SqliteConnectionFactory(
            new SqliteConnectionOptions { DatabasePath = databasePath }).OpenConnection();

        try
        {
            await factory.DisposeAsync();

            // THE CONTROL: with a handle held open, the directory must survive and the assertion
            // the three tests above rely on must reject it. If this ever stops throwing, those
            // three have stopped proving anything.
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(
                () => AssertDirectoryWasDeleted(factory.LastDeleteFailure, configDirectory));
        }
        finally
        {
            leaked.Dispose();

            // Clean up what the planted leak left behind, so this control does not itself become a
            // contributor to the ~22,000 leaked directories it exists to prevent.
            SqlitePoolCleaner.ClearPoolsFor(databasePath);
            Arbitarr.TestSupport.SqlitePools.ClearPoolsForDirectory(configDirectory);

            try
            {
                if (Directory.Exists(configDirectory))
                {
                    Directory.Delete(configDirectory, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: cleanup must never fail an otherwise-green run.
            }
        }
    }

    /// <summary>
    /// The shared assertion: disposal removed the directory and recorded no failure doing so.
    ///
    /// <para>Both conditions are checked, and neither is redundant. <c>LastDeleteFailure</c> names
    /// the handle that won, which is the diagnostic a future regression needs; the directory check
    /// is the property itself, and would still catch a delete that failed without recording
    /// anything.</para>
    /// </summary>
    private static void AssertDirectoryWasDeleted(Exception? lastDeleteFailure, string configDirectory)
    {
        Assert.True(
            lastDeleteFailure is null,
            "Disposal could not delete the config directory. Until arb-dhua this was the normal outcome, " +
            "caused by ArbitarrDbContextOptionsFactory handing EF an open connection WITHOUT ownership, so " +
            "no disposed context ever returned its connection to the pool and no pool clear could reach it. " +
            "Check that Create still passes contextOwnsConnection: true, and that the factory still clears " +
            "the pools before deleting — both halves are required.\n" +
            $"  Recorded failure: {lastDeleteFailure?.GetType().Name}: {lastDeleteFailure?.Message}");

        Assert.False(
            Directory.Exists(configDirectory),
            $"Disposal reported no failure but the config directory is still present: {configDirectory}");
    }
}
