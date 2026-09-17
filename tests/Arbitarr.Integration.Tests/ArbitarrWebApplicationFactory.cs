using Arbitarr.Data;
using Arbitarr.Data.Logging;
using Arbitarr.Host.Maintenance;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Hosts the real <c>Arbitarr.Host</c> composition root in-process (<c>Program.cs</c>, unmodified)
/// against a fresh, per-instance SQLite file under a temp <c>/config</c> directory, so M2's
/// dashboard endpoints, migrations-on-startup behaviour, and static file serving are all exercised
/// exactly as they run in production. Callers seed rows via <see cref="SeedAsync"/> before issuing
/// requests through <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>.
///
/// <para><b>xUnit tears this down through <c>Dispose</c> only, never <c>DisposeAsync</c>, and that
/// is safe (arb-000x).</b> This type is injected as an <c>IClassFixture</c> into ~29 classes in
/// this assembly. xunit 2.9.2 disposes a class fixture through <c>IDisposable</c> only — it never
/// calls a fixture's own <see cref="IAsyncDisposable.DisposeAsync"/>, so for every one of those ~29
/// classes only <see cref="Dispose(bool)"/> below ever runs, not <see cref="DisposeAsync"/>. That is
/// safe here because <see cref="Dispose(bool)"/> is overridden to drain the host (via
/// <c>base.Dispose(disposing)</c>) before deleting the config directory — the same drain-then-delete
/// order <see cref="DisposeAsync"/> uses, just synchronous rather than awaited. Both orderings are
/// pinned: <see cref="HostDisposalDrainsBackgroundWorkTests.The_synchronous_disposal_path_also_stops_writing"/>
/// (mutation-proved) covers the drain, and
/// <see cref="ConfigDirectoryIsDeletedOnDisposalTests.The_synchronous_disposal_path_deletes_the_config_directory"/>
/// covers the delete.</para>
///
/// <para><b>Do NOT add <c>IAsyncLifetime</c> to this class.</b> xunit prepends rather than replaces:
/// a fixture that implements <c>IAsyncLifetime</c> gets <c>DisposeAsync</c> called AND THEN
/// <c>Dispose</c> — not one or the other. Since <see cref="Dispose(bool)"/> already deletes the
/// directory, adding <c>IAsyncLifetime</c> would make every one of the ~29 consumers delete it
/// TWICE per run, for no behavioural benefit (the ordering property already holds on the sync
/// path). Worse, it would make <see cref="DisposeAsync"/> look load-bearing for these classes when
/// it is not, inviting a future edit to remove the <see cref="Dispose(bool)"/> override as
/// "redundant" — which would silently break the ~29 classes that never call
/// <see cref="DisposeAsync"/> in the first place. See
/// [process.md Coverage expectations](../../docs/standards/process.md#coverage-expectations) for
/// the general xunit-lifetime rule this instance follows.</para>
/// </summary>
public sealed class ArbitarrWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _configDirectory;

    /// <summary>
    /// Whether this factory DELETES <see cref="ConfigDirectory"/> on disposal. True for the
    /// per-instance directory the parameterless constructor invents (nobody else can be using it);
    /// false for a caller-supplied one, which the caller owns and outlives this host — see
    /// <see cref="OverConfigDirectory"/>.
    /// </summary>
    private readonly bool _ownsConfigDirectory;

    /// <summary>
    /// A host over a FRESH, per-instance config directory — the default, and what every test that
    /// does not explicitly need otherwise should use.
    ///
    /// <para><b>THIS MUST REMAIN THE ONLY PUBLIC CONSTRUCTOR.</b> xUnit rejects a class fixture type
    /// that declares more than one ("may only define a single public constructor"), and ~29 classes
    /// in this assembly inject this type as an <c>IClassFixture</c> — so adding a second public
    /// constructor fails all of them at once rather than anything local to the change. That is why
    /// the caller-supplied-directory overload is private behind
    /// <see cref="OverConfigDirectory"/>.</para>
    /// </summary>
    public ArbitarrWebApplicationFactory()
        : this(Path.Combine(Path.GetTempPath(), "arbitarr-m2-tests", Guid.NewGuid().ToString("N")), ownsConfigDirectory: true)
    {
    }

    private ArbitarrWebApplicationFactory(string configDirectory, bool ownsConfigDirectory)
    {
        _configDirectory = configDirectory ?? throw new ArgumentNullException(nameof(configDirectory));
        _ownsConfigDirectory = ownsConfigDirectory;
    }

    /// <summary>
    /// A host over a CALLER-SUPPLIED config directory, so a test can build a second host over the
    /// SAME database file and assert what survives a restart (arb-v3w).
    ///
    /// <para>A static factory rather than a constructor for the reason stated on the parameterless
    /// constructor above: a second PUBLIC constructor breaks every class-fixture consumer in this
    /// assembly.</para>
    ///
    /// <para><b>Such a host does NOT delete the directory on disposal, and that is load-bearing.</b>
    /// The whole point is that a SECOND host reads what the first one wrote, so a first host that
    /// took the database with it on the way out would make the restart assertion test nothing —
    /// the second host would rehydrate from an empty database and report no items for the same
    /// reason a correct implementation would have reported them. Disposal still stops the host and
    /// clears the connection pools; only the delete is skipped. THE CALLER MUST DELETE IT.</para>
    ///
    /// <para>The derived <c>Arbitarr:ReleaseGuidSecret</c> is a function of this path, so two hosts
    /// over one directory agree on it — which is what makes a restart test meaningful rather than
    /// one that silently changes the secret under itself.</para>
    /// </summary>
    public static ArbitarrWebApplicationFactory OverConfigDirectory(string configDirectory) =>
        new(configDirectory, ownsConfigDirectory: false);

    /// <summary>The per-instance <c>/config</c> directory this host was given.</summary>
    public string ConfigDirectory => _configDirectory;

    /// <summary>
    /// The exception from the FINAL delete attempt during disposal, or null when cleanup succeeded.
    ///
    /// <para><b>This is now expected to be NULL, and is ASSERTED ON</b> by
    /// <see cref="ConfigDirectoryIsDeletedOnDisposalTests"/> (arb-dhua). It was previously
    /// diagnostics-only because the directory was not deletable in-process at all; that is fixed at
    /// the mechanism — see <c>ArbitarrDbContextOptionsFactory.Create</c>, which hands EF ownership of
    /// the connection (arb-dhua) and hands it over CLOSED so EF owns it even when the context is
    /// never used (arb-auam), so a disposed context RETURNS it to the pool and the clear below can
    /// close it.</para>
    ///
    /// <para>It stays an exposed property rather than becoming an in-disposal throw: a cleanup
    /// failure must not fault an otherwise-green run from inside <c>Dispose</c>, where it would
    /// surface on whichever unrelated test happened to be in flight. The assertion belongs in a
    /// test that owns its own factory.</para>
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
    /// ones (so two factories cannot collide on guids), and adequate because it never leaves the
    /// test process. Not "no weaker than the random secret it replaces": it is deterministic from a
    /// knowable input, so it is weaker, and the reason that is fine is the confinement, not the
    /// derivation.
    /// </summary>
    private static string ReleaseGuidSecretForConfigDirectory(string configDirectory) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configDirectory)));

    /// <summary>
    /// Runs <paramref name="seed"/> against a fresh scoped <see cref="ArbitarrDbContext"/> and saves
    /// changes.
    ///
    /// <para><b>arb-tdc4: a <see cref="SqliteException"/> here is rethrown with diagnostics, not
    /// swallowed or retried.</b> CI has seen an intermittent Error 5 ("database is locked") from this
    /// path with no local repro after two rounds (see the bead), so the only thing left to improve is
    /// what the NEXT sighting reveals. <see cref="SeedDiagnostics.Wrap"/> does the wrapping; kept as a
    /// separate TestSupport helper so it has its own unit tests rather than only being exercised
    /// end-to-end through a flaky integration failure.</para>
    /// </summary>
    public async Task SeedAsync(Func<ArbitarrDbContext, Task> seed)
    {
        // Force host startup (and its Database.Migrate() call) before seeding against the same schema.
        using var client = CreateClient();

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();

        var maintenanceFirstPassCompleted = IsMaintenanceFirstPassCompleted();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await seed(dbContext);
            await dbContext.SaveChangesAsync();
        }
        catch (SqliteException ex)
        {
            stopwatch.Stop();
            throw SeedDiagnostics.Wrap(ex, stopwatch.Elapsed, maintenanceFirstPassCompleted, _configDirectory);
        }
    }

    /// <summary>
    /// Whether this host's <see cref="MaintenanceHostedService"/> had already published
    /// <see cref="MaintenanceHostedService.FirstPassCompleted"/> at the moment this was called.
    ///
    /// <para><b>Read via <c>Task.IsCompleted</c>, never awaited.</b> The whole point is to capture a
    /// point-in-time snapshot of whether the automatic backup that pass takes had finished BEFORE the
    /// seed ran — awaiting the task would itself wait for the pass, which defeats the diagnostic (it
    /// would always report "completed" once observed). Modelled on
    /// <see cref="WaitForFirstMaintenancePassAsync"/>'s own lookup, but that method's throwing
    /// behaviour on a missing registration is deliberately NOT reused here: a diagnostics path must
    /// not itself become a new way for seeding to fail, so a service that cannot be found or resolved
    /// reports null (unknown) rather than throwing.</para>
    /// </summary>
    private bool? IsMaintenanceFirstPassCompleted()
    {
        try
        {
            var service = Services.GetServices<IHostedService>()
                .OfType<MaintenanceHostedService>()
                .SingleOrDefault();

            return service?.FirstPassCompleted.IsCompleted;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
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
    /// <para><b>The delete NOW SUCCEEDS, and that is asserted (arb-dhua).</b> For most of this
    /// file's life it did not: these factories had NEVER deleted their directories (~12,354 leaked
    /// under <c>arbitarr-m2-tests</c> and ~9,814 under <c>arbitarr-remote-address-tests</c>), and an
    /// untouched pre-existing test leaked one while passing green. The old code caught
    /// <see cref="IOException"/> and moved on, which is how it stayed invisible.</para>
    ///
    /// <para><b>The cause was ownership, not the pool inventory</b> — which is why three attempts at
    /// widening the set of connection STRINGS all failed. <c>ArbitarrDbContextOptionsFactory</c>
    /// opens a connection eagerly and handed it to <c>UseSqlite(connection)</c>, whose default
    /// leaves ownership with the CALLER: disposing the context did not dispose the connection, so it
    /// was never RETURNED to the pool. <c>ClearPool</c> closes what a pool HOLDS, and a connection
    /// that never came back is not among them — so no clear at any scope could reach it. That is
    /// fixed at the mechanism, with <c>contextOwnsConnection: true</c>. Note that ownership alone is
    /// not sufficient: disposal only returns the handle to the pool, which still holds a share lock
    /// on Windows, so the clear below is the other half and BOTH are required.</para>
    ///
    /// <para><b>Why the retry loop stays.</b> It is no longer load-bearing for the EF connection,
    /// but a directory can still be momentarily locked by something outside this factory's control
    /// (a virus scanner, an indexer). The final failure is recorded rather than swallowed so a
    /// regression is legible instead of silent.</para>
    ///
    /// <para><b>Division of labour with <see cref="HostDisposalDrainsBackgroundWorkTests"/>.</b>
    /// That class asserts the directory is QUIESCENT after disposal — arb-rwhb's property, about
    /// background work being drained. Deletion is a separate property with its own test
    /// (<see cref="ConfigDirectoryIsDeletedOnDisposalTests"/>); keeping them apart means a
    /// regression in either is attributable to the change that caused it.</para>
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        // arb-s3ky: BEFORE base.DisposeAsync, because Services is gone afterwards. See the helper.
        var drainCompletions = CaptureLogDrainCompletions();

        // Stops the host and awaits its hosted services, so no detached backup is still reading the
        // database when the directory goes. This is the ordering the whole fix rests on.
        await base.DisposeAsync().ConfigureAwait(false);

        DeleteConfigDirectory(drainCompletions);
    }

    protected override void Dispose(bool disposing)
    {
        // arb-s3ky: BEFORE base.Dispose, for the same reason as in DisposeAsync. This is the path
        // the ~29 class-fixture consumers actually take (see the class remarks), so wiring only the
        // async override would leave the race live for almost every test in this assembly.
        var drainCompletions = disposing ? CaptureLogDrainCompletions() : null;

        // base.Dispose stops the host synchronously. It cannot await hosted services the way
        // DisposeAsync does, which is why DeleteConfigDirectory retries rather than trying once.
        base.Dispose(disposing);

        if (disposing)
        {
            DeleteConfigDirectory(drainCompletions);
        }
    }

    /// <summary>
    /// arb-km0a: waits until this host's <see cref="MaintenanceHostedService"/> has FINISHED its
    /// first maintenance pass. A test calls this before reading any state that pass writes.
    ///
    /// <para><b>What it is for.</b> <c>BackgroundService.StartAsync</c> returns at
    /// <c>ExecuteAsync</c>'s first await, so starting the host does NOT mean the first pass has run.
    /// That pass takes an automatic configuration backup immediately (before the first
    /// <c>Task.Delay</c>, deliberately), and a failure it records lands in <c>BackupStateStore</c>
    /// whenever it happens to finish — which for three observed runs was in the middle of an
    /// unrelated assertion. Awaiting the service's own published completion is the only thing that
    /// establishes the pass is DONE rather than merely started; a delay would only move the race.</para>
    ///
    /// <para><b>Reached through <c>GetServices&lt;IHostedService&gt;()</c> rather than by resolving
    /// the type.</b> <c>AddHostedService</c> registers the concrete type only as an
    /// <c>IHostedService</c>, so <c>GetRequiredService&lt;MaintenanceHostedService&gt;()</c> does not
    /// resolve it. Same shape as <c>ThrottledRecorderHostedCompositionTests</c>.</para>
    ///
    /// <para><b>BOUNDED, and it FAILS rather than continuing.</b> Modelled on
    /// <c>ConfigDirectoryTeardown.DrainCompletionWait</c>: an unbounded await would turn a
    /// regression in the seam into a hung test run with no attribution, and a silent give-up would
    /// restore the very race this exists to remove while reporting green. So the bound elapsing
    /// throws and names the service, which is a legible failure pointing at the one place that can
    /// have broken. It is not a sleep: the normal path returns as soon as the pass publishes,
    /// typically in milliseconds, and the bound is never reached.</para>
    /// </summary>
    public async Task WaitForFirstMaintenancePassAsync()
    {
        // Force host startup, so the service exists to be found. Calling this before any client has
        // been created would otherwise resolve Services and start the host as a side effect anyway;
        // doing it explicitly makes the dependency visible rather than incidental.
        using var client = CreateClient();

        var service = Services.GetServices<IHostedService>()
            .OfType<MaintenanceHostedService>()
            .SingleOrDefault();

        if (service is null)
        {
            throw new InvalidOperationException(
                "This host registered no MaintenanceHostedService, so its first maintenance pass " +
                "cannot be awaited. Program.cs registers it unconditionally, so a null here means " +
                "the registration was removed or made conditional -- not that the wait is optional.");
        }

        var completed = await Task.WhenAny(
            service.FirstPassCompleted,
            Task.Delay(FirstMaintenancePassWait)).ConfigureAwait(false);

        if (completed != service.FirstPassCompleted)
        {
            throw new TimeoutException(
                $"MaintenanceHostedService did not publish FirstPassCompleted within {FirstMaintenancePassWait}. " +
                "That task is completed from a finally covering every exit of the first pass, " +
                "including cancellation and an unexpected throw, so failing to see it means the " +
                "publication was removed or the pass is wedged on a step that never returns.");
        }
    }

    /// <summary>
    /// The bound on <see cref="WaitForFirstMaintenancePassAsync"/>. Generous on purpose: the first
    /// pass takes a real SQLite backup, which on a loaded parallel runner is not instant, and this
    /// number exists to convert a HANG into a named failure rather than to police how long a
    /// healthy pass may take. Matches <c>ConfigDirectoryTeardown.DrainCompletionWait</c>.
    /// </summary>
    private static readonly TimeSpan FirstMaintenancePassWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The <c>DrainCompleted</c> task of every <see cref="SqliteLoggerProvider"/> this host
    /// registered, read from <see cref="WebApplicationFactory{TEntryPoint}.Services"/> (arb-s3ky).
    ///
    /// <para><b>THIS MUST RUN BEFORE <c>base.Dispose</c>/<c>base.DisposeAsync</c>.</b> Those tear the
    /// host down and <c>Services</c> is unusable afterwards, so the instances have to be captured
    /// while the provider is still alive. The tasks themselves outlive it, which is exactly the
    /// property that makes them worth capturing: the pump can still be draining after the host is
    /// gone, and a task is still awaitable then.</para>
    ///
    /// <para><b>The providers are DISPOSED here, and that is what makes the completion reachable.</b>
    /// <c>DrainCompleted</c> publishes only once the pump has run its FINAL drain, and the pump only
    /// reaches that drain once it observes cancellation — which is to say once
    /// <c>SqliteLoggerProvider.Dispose</c> has been called. Capturing the task WITHOUT disposing
    /// therefore hands the teardown a completion nothing will ever complete — measured as every
    /// fixture burning the full <c>ConfigDirectoryTeardown.DrainCompletionWait</c> bound (four
    /// disposal tests took 2m35s, ~30s each) and, worse, a still-live pump writing into the
    /// directory being deleted, which crashed the host with
    /// <c>SQLite Error 14: unable to open database file</c>. Dispose is bounded and idempotent, so
    /// calling it here stays safe if any other path also disposes.</para>
    ///
    /// <para><b>arb-2t9u: this is no longer the ONLY caller, and it still must not be removed.</b>
    /// <c>LoggingSetup.AddArbitarrSqliteLogging</c> now registers the provider as a FACTORY, so the
    /// container owns it and disposes it during host teardown — which is the whole point of that
    /// bead, and it means the host would eventually cancel the pump on its own. It does so INSIDE
    /// <c>base.Dispose</c>, i.e. after this method has already had to run: the completions have to be
    /// captured from <c>Services</c> while the host is still alive, and a completion captured from a
    /// provider that has not been disposed yet is one this teardown would then have to wait out. So
    /// the explicit dispose here is now about ORDER rather than about being the only disposer, and
    /// the resulting double dispose is safe by construction — <c>SqliteLoggerProvider.Dispose</c> is
    /// idempotent through an explicit latch, pinned by
    /// <c>SqliteLoggerProviderTests.Disposing_twice_does_not_throw_and_leaves_the_completion_published</c>.</para>
    ///
    /// <para>Returns null on ANY failure to resolve rather than throwing. Some hosts in this assembly
    /// never start (a test that only builds the factory), and a disposal path must not fault an
    /// unrelated in-flight test — that is the same reasoning that makes
    /// <see cref="LastDeleteFailure"/> a recorded property rather than a throw. A null here just
    /// means the teardown falls back to the per-attempt pool clear it always had.</para>
    /// </summary>
    private IReadOnlyList<Task>? CaptureLogDrainCompletions()
    {
        try
        {
            var providers = Services.GetServices<ILoggerProvider>()
                .OfType<SqliteLoggerProvider>()
                .ToArray();

            foreach (var provider in providers)
            {
                provider.Dispose();
            }

            return providers.Select(provider => provider.DrainCompleted).ToArray();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the per-instance config directory. This SUCCEEDS, and
    /// <see cref="ConfigDirectoryIsDeletedOnDisposalTests"/> asserts it does — see the remarks on
    /// <see cref="DisposeAsync"/> for the ownership defect that made it impossible until arb-dhua.
    /// </summary>
    private void DeleteConfigDirectory(IReadOnlyList<Task>? drainCompletions)
    {
        // The pool-clear-then-delete sequence, and the full account of WHY BOTH HALVES ARE REQUIRED
        // (arb-dhua) and why ClearAllPools is banned regardless, now live on
        // ConfigDirectoryTeardown. It is one implementation shared with the other factory and with
        // every test class that builds its own config directory, because for as long as the block
        // was copied per caller most callers carried only one half of it or neither (arb-gphi).
        //
        // THE POOL CLEARS RUN FOR EVERY HOST, owned directory or not (arb-v3w): releasing this
        // host's file handles is what lets a SECOND host -- or the caller's own cleanup -- open the
        // same database afterwards. Only the DELETE is ownership-gated. The two halves come apart
        // here for the one legitimate reason they ever do: a non-owning host's caller deletes later,
        // so this is a complete operation with a later partner, not the half-implemented pairing
        // arb-gphi removed (which deleted WITHOUT clearing, and so silently could not work).
        if (!_ownsConfigDirectory)
        {
            ConfigDirectoryTeardown.ClearPools(_configDirectory);
            return;
        }

        // TryDelete rather than Delete: this runs from Dispose, where a throw would fault whichever
        // unrelated test is in flight rather than the one that owns this factory. The failure is
        // RECORDED instead and ConfigDirectoryIsDeletedOnDisposalTests turns it into a visible
        // failure. Since arb-dhua a non-null result here is an ANOMALY, not the normal path.
        //
        // arb-s3ky: the drain completions captured above are passed through, so the delete waits for
        // the log sink's pump to FINISH rather than merely to have been asked to stop. The pool
        // clears alone cannot cover that: SqliteLoggerProvider.Dispose's bound gives up on purpose,
        // and a handle the pump opens after a clear is one no retry could win.
        LastDeleteFailure = ConfigDirectoryTeardown.TryDelete(_configDirectory, drainCompletions);
    }
}
