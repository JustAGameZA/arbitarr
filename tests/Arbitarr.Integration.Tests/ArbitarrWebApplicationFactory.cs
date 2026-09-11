using Arbitarr.Data;
using Arbitarr.Data.Backup;
using Arbitarr.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Hosts the real <c>Arbitarr.Host</c> composition root in-process (<c>Program.cs</c>, unmodified)
/// against a fresh, per-instance SQLite file under a temp <c>/config</c> directory, so M2's
/// dashboard endpoints, migrations-on-startup behaviour, and static file serving are all exercised
/// exactly as they run in production. Callers seed rows via <see cref="SeedAsync"/> before issuing
/// requests through <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>.
/// </summary>
public sealed class ArbitarrWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-m2-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The per-instance <c>/config</c> directory this host was given.</summary>
    public string ConfigDirectory => _configDirectory;

    /// <summary>
    /// The exception from the FINAL delete attempt during disposal, or null when cleanup succeeded.
    /// Today this is nearly always non-null — see arb-dhua and <see cref="DisposeAsync"/>; the
    /// directory is not deletable in-process. Exposed as DIAGNOSTICS, not as a property to assert
    /// on: it is what lets whoever picks up arb-dhua see which handle held the directory, rather
    /// than having to re-derive the leak from an empty %TEMP% listing.
    /// </summary>
    public Exception? LastDeleteFailure { get; private set; }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_configDirectory);

        // UseSetting, NEVER Environment.SetEnvironmentVariable. The env var is process-wide, so
        // with several hosts alive in one process the last writer wins and a host can end up
        // opening a neighbour's SQLite file. This setting belongs to this builder alone, which is
        // what lets the assembly run its classes in parallel. Program.cs reads Arbitarr:ConfigDir
        // ahead of the env var precisely so this wins.
        builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);

        // arb-0hd0, and a UseSetting for the same reason as the line above: ReleaseGuid's HMAC
        // secret is a mutable PROCESS-GLOBAL (ReleaseGuid._hmacKey), and Program.cs rewrote it via
        // ReleaseGuid.Configure on every host build. With classes running in parallel, a second
        // host starting mid-request changed the secret under the first host, whose search had
        // already computed a lookup key with the old one -- the issued link then matched neither
        // the memory tier nor the store, and the download returned 404 with no exception and no
        // log (arb-agh, four occurrences; the two hosts were 0.5 ms apart in the trace).
        //
        // Supplying the secret here makes Program.cs use this value instead of generating one per
        // config directory, so a host build stops being a rewrite of the global with a NEW value.
        // Derived from the config directory rather than random so that a factory rebuilding a host
        // for the same directory reproduces the same secret -- which is what the persisted-secret
        // file gives production across restarts, and what tests that restart a host depend on.
        //
        // This does NOT make the static safe on its own, and must not be mistaken for the fix: two
        // factories still write different values to the same global. The fix is that
        // RenderedRelease.ProxyGuid is now computed once per instance, so a request's five
        // evaluations agree even mid-swap. This just stops tests provoking the swap needlessly.
        //
        // Note Program.cs still CREATES the persisted secret file even when this setting is given,
        // and that is deliberate: BackupService copies release-guid-secret.key into every archive
        // unconditionally, so a host that skipped creating it answered 500 on the backup download.
        // Supplying this key overrides the in-memory value only; it must never be turned into a
        // reason to skip ReleaseGuidSecretFile.LoadOrCreate.
        builder.UseSetting("Arbitarr:ReleaseGuidSecret", ReleaseGuidSecretForConfigDirectory(_configDirectory));
    }

    /// <summary>
    /// A stable 32-byte secret for a config directory, base64-encoded as the configuration key
    /// expects. SHA-256 of the path: deterministic for the same directory, different for different
    /// ones (so two factories cannot collide on guids), and no weaker than the random secret it
    /// replaces for a test-only value that never leaves this process.
    /// </summary>
    private static string ReleaseGuidSecretForConfigDirectory(string configDirectory) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configDirectory)));

    /// <summary>Runs <paramref name="seed"/> against a fresh scoped <see cref="ArbitarrDbContext"/> and saves changes.</summary>
    public async Task SeedAsync(Func<ArbitarrDbContext, Task> seed)
    {
        // Force host startup (and its Database.Migrate() call) before seeding against the same schema.
        using var client = CreateClient();

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        await seed(dbContext);
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// arb-rwhb: THE HOST MUST BE FULLY STOPPED BEFORE THE CONFIG DIRECTORY IS DELETED, and that
    /// ordering — not the per-instance directory this factory already had — is what fixes the
    /// shard-A2 intermittent.
    ///
    /// <para><b>What went wrong.</b> <c>MaintenanceHostedService</c> (Program.cs:783, registered
    /// unconditionally, suppressed by no test host) runs its first maintenance pass IMMEDIATELY:
    /// <c>ExecuteAsync</c> calls <c>RunAutomaticBackupAsync</c> before its first
    /// <c>Task.Delay(interval)</c>, so a long configured interval does not defer it. The backup is
    /// on by default (<c>AutomaticBackupRetainedCount</c> defaults to 7), and it reaches
    /// <c>BackupService.SnapshotDatabase</c>, which opens a pooled connection to the live database
    /// and calls the blocking <c>SqliteConnection.BackupDatabase</c>. That work runs on a detached
    /// <c>BackgroundService</c> task that nothing awaited — so the old <c>Dispose</c> deleted this
    /// directory out from under a live SQLite reader.</para>
    ///
    /// <para>The visible damage was never in this class. The orphaned task's continuation resolved
    /// services from an already-disposed provider, and with <c>maxParallelThreads: 4</c> that
    /// surfaced on whichever test happened to be in flight — twice on <c>ConfigMaskingTests</c>,
    /// which failed with <c>ObjectDisposedException</c> before evaluating a single assertion. The
    /// owning host logged <c>SQLite Error 5898: 'disk I/O error'</c> from <c>BackupDatabase</c>,
    /// which is what SQLite reports when the files are removed mid-read.</para>
    ///
    /// <para><b>Why both disposal paths are overridden.</b> Callers use both shapes today
    /// (<c>await using</c> in AdminBackupEndpointsTests/AdminSettingsEndpointsTests, plain
    /// <c>using</c> in OllamaClassificationErrorStatusTests), and xunit disposes the ~29 injected
    /// class fixtures itself. Overriding only one leaves the race live on the other.
    /// <c>DisposeAsync</c> is the honest path: the base implementation stops the host and awaits its
    /// hosted services, so by the time the delete runs there is no backup in flight. The sync path
    /// cannot await, so it relies on the retry below. (Note that for DELETION the two paths behave
    /// identically — both leave the directory behind, for the reason given under arb-dhua; the
    /// difference between them is whether background work has been drained, which is the property
    /// that matters here.)</para>
    ///
    /// <para><b>KEEP IN STEP WITH <see cref="RemoteAddressWebApplicationFactory"/>.</b> That factory
    /// hosts the same composition root and carried the byte-identical unfixed pattern, so it has the
    /// same pair of overrides. A fix applied to only one of the two leaves the race live in the
    /// other.</para>
    ///
    /// <para><b>The delete is BEST-EFFORT and is expected to FAIL — see arb-dhua.</b> The previous
    /// code caught <see cref="IOException"/> and moved on, which is how this stayed invisible: a
    /// directory left behind is silent, and the next run's <c>StagingSweepService</c> tidies it.
    /// Measuring it showed these factories have NEVER deleted their directories — ~12,354 leaked
    /// under <c>arbitarr-m2-tests</c> and ~9,814 under <c>arbitarr-remote-address-tests</c>, and an
    /// untouched pre-existing test leaks one while passing green. The <c>arbitarr.db</c> handle is
    /// held for the TEST-PROCESS lifetime: every <c>ArbitarrDbContext</c> holds a connection EF has
    /// already CHECKED OUT of the pool, and a pool clear closes only idle RETURNED connections — so
    /// no pool clear, at any scope, can release it. The retry below therefore cannot win. It is kept
    /// only so that the case it CAN win is not lost, and the final failure is now recorded rather
    /// than swallowed.</para>
    ///
    /// <para><b>What this fix does and does not claim.</b> Draining the host before deleting is
    /// correct and is what stops background work touching the directory — but it is NOT sufficient
    /// to delete it, and never was. That is why
    /// <see cref="HostDisposalDrainsBackgroundWorkTests"/> asserts the directory is QUIESCENT after
    /// disposal rather than GONE: removal is arb-dhua's problem. An assertion on removal would fail
    /// for a reason this change is not responsible for.</para>
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        // Stops the host and awaits its hosted services, so no detached backup is still reading the
        // database when the directory goes. This is the ordering the whole fix rests on.
        await base.DisposeAsync().ConfigureAwait(false);

        DeleteConfigDirectory();
    }

    protected override void Dispose(bool disposing)
    {
        // base.Dispose stops the host synchronously. It cannot await hosted services the way
        // DisposeAsync does, which is why DeleteConfigDirectory retries rather than trying once.
        base.Dispose(disposing);

        if (disposing)
        {
            DeleteConfigDirectory();
        }
    }

    /// <summary>
    /// BEST-EFFORT removal of the per-instance config directory. This is EXPECTED TO FAIL and
    /// leave the directory behind — see arb-dhua, and the remarks on <see cref="DisposeAsync"/>.
    /// Nothing asserts on its success, and nothing should: the attempt is kept so the directory is
    /// removed in whatever cases it can be, and so the reason it could not is recorded.
    /// </summary>
    private void DeleteConfigDirectory()
    {
        // WHY THE POOL CLEARS ARE HERE, AND WHY THEY ARE NOT ENOUGH (arb-dhua).
        //
        // Draining the host stops the WORK; it does not release the FILE HANDLES. These two calls
        // return what they can of the pooled handles, which is worth doing and costs nothing — but
        // they do NOT release the handle that actually holds the directory, and no variation on
        // them will:
        //
        //   ClearPool/ClearAllPools close only IDLE, RETURNED connections. Every ArbitarrDbContext
        //   holds a connection EF has already CHECKED OUT of the pool, and a checked-out connection
        //   is not the pool's to close. That is why widening the clear has already failed twice —
        //   first SqlitePools.ClearPoolsForDirectory, then SqlitePoolCleaner.ClearPoolsFor walking
        //   DatabaseConnectionStrings.ForDatabase. The problem was never WHICH connection strings
        //   were enumerated, so do not spend a third attempt widening the inventory: it is complete.
        //
        // Never SqliteConnection.ClearAllPools() regardless: the process-global form force-closes
        // pooled connections belonging to test classes running in parallel (arb-cbc/arb-5ba) and is
        // banned from test IL with no allow-list by
        // Arbitarr.Architecture.Tests.TestProcessGlobalStateTests. It would not help here anyway —
        // it cannot touch a checked-out connection either.
        //
        // Via BackupPaths rather than a literal "arbitarr.db": that type owns where the database
        // lives, and a second spelling of the name is exactly what CLAUDE.md §1 warns silently
        // stops covering the file when it moves.
        SqlitePoolCleaner.ClearPoolsFor(new BackupPaths(_configDirectory).DatabasePath);

        // The log store is a SECOND database (arbitarr-logs.db) with its own connection shape, and
        // it sits under the same directory — so clearing only the main one leaves the delete losing
        // to a log handle instead. ClearPoolsForDirectory covers it and anything a test restored
        // beside them.
        SqlitePools.ClearPoolsForDirectory(_configDirectory);

        const int attempts = 10;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(_configDirectory))
                {
                    return;
                }

                Directory.Delete(_configDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // UnauthorizedAccessException as well as IOException: Windows raises that one for a
                // file another handle still has open, and catching only IOException let it escape.
                if (attempt == attempts)
                {
                    // Recorded rather than swallowed silently. Today this arm is the NORMAL path,
                    // not an anomaly (arb-dhua): the directory survives every run. The failure is
                    // still TOLERATED — a locked file must not fail a green run — but it is now
                    // legible, so the next person to look does not re-derive the leak from scratch
                    // the way this one had to.
                    LastDeleteFailure = ex;
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }
}
