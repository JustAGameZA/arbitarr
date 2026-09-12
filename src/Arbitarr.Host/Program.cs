using Arbitarr.Ai;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Dashboard;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Routing;
using Arbitarr.Api.Search;
using Arbitarr.Api.Security;
using Microsoft.AspNetCore.Identity;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Caching;
using Arbitarr.Data.CircuitBreaker;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Maintenance;
using Arbitarr.Data.Search;
using Arbitarr.Data.Security;
using Arbitarr.Data.Settings;
using Arbitarr.Data.Sources;
using Arbitarr.Host;
using Arbitarr.Host.Caching;
using Arbitarr.Host.Security;
using Arbitarr.Host.Sources;
using Arbitarr.Sources.NzbHydra;
using Microsoft.EntityFrameworkCore;

// Arbitarr.Host is the explicit composition root: the only project permitted to
// reference source-adapter and other outer-layer projects (AC6). Currently minimal —
// other steps extend DI wiring and config binding here.
var builder = WebApplication.CreateBuilder(args);

// Issue #69: WebApplicationBuilder adds the Windows EventLog provider by default, and that provider
// holds a process-wide native listener shared by every host in the process. Test runs boot and
// dispose many hosts in the same process, so one host's disposal can tear the listener down while
// another is still writing through it — surfacing as an ObjectDisposedException from
// EventLogInternal, intermittently and only under load. Arbitarr never runs as a Windows service
// (it ships as a Linux container), so nothing reads this sink in any supported deployment and
// removing it costs no diagnostics. Removed here in the composition root rather than in each test
// factory because there are five host-construction sites in the test suite and a sixth added later
// would silently reintroduce the race. ClearProviders drops Console and Debug too, so both are
// re-added explicitly below and container log output via docker logs is unchanged.
//
// #65 added a THIRD provider to this curated list — the SQLite sink behind the System page's Logs
// tab. It is registered further down rather than here only because it needs configDirectory, which
// is not computed until below; see that registration for its own reasoning. The point of this
// comment stands unchanged: the provider list is deliberate, and every entry in it is explained.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Runtime state lives under /config (AC21), overridable for local dev/test so a real /config
// directory is never required outside the production container.
//
// READ ORDER IS LOAD-BEARING and the configuration key must stay FIRST. The env var is
// process-wide, so when several hosts run in one process — which is every test run — the last
// writer wins and a host can open a neighbour's database. `Arbitarr:ConfigDir` is per-builder, so
// `builder.UseSetting("Arbitarr:ConfigDir", dir)` gives each host its own directory with no shared
// mutable state; that is what lets Integration.Tests run its classes in parallel. Swapping the two
// reads back would silently reintroduce the race, because the env var an unrelated test set would
// then override the value this host was explicitly handed.
//
// ARBITARR_CONFIG_DIR is retained and unchanged for production and local dev (it is the documented
// knob in README.md). `builder.Configuration` does read environment variables, but only under the
// double-underscore convention the other knobs use (`ARBITARR__CONFIGDIR`); this single-underscore
// legacy name binds to nothing, so it is still read explicitly here.
var configDirectory = builder.Configuration["Arbitarr:ConfigDir"]
    ?? Environment.GetEnvironmentVariable("ARBITARR_CONFIG_DIR")
    ?? "/config";
Arbitarr.Host.Provisioning.DatasetProvisioner.EnsureProvisioned(configDirectory);
var databasePath = Path.Combine(configDirectory, "arbitarr.db");

// #65: the application-log sink, the third entry in the curated provider list at the top of this
// file (see the #69 comment there). A SEPARATE SQLite file from arbitarr.db above — LogStore's
// remarks carry the full reasoning: log writes are bursty and would contend with the D1-critical
// search path for the main database's writer lock, and #56's configuration backup must not drag a
// log store around with it.
//
// Registered before Build() so the provider exists for startup logging, and EnsureCreated() runs
// here — synchronously, once — so no log write can ever race schema creation. This call is also
// arb-itmm's converter for THIS file: it is the single-threaded first opener, before Build() and
// long before any hosted service can open arbitarr-logs.db concurrently, so LogStore.OpenConnection
// re-issuing "PRAGMA journal_mode = WAL" on every later open only ever hits an already-WAL file
// (see LogStore.OpenConnection's remarks). Moving this call after Build() or off this thread would
// reopen that unbounded wait for the log store.
//
// ARBITARR_LOGDB_ENABLED=false turns the sink off, the equivalent of Sonarr's LogDbEnabled. Console
// is unaffected either way: docker logs is the raw view and must not regress, so this sink is
// strictly additive to it. LogStore is registered regardless of the toggle so GET /api/admin/logs
// still serves (an empty page, and previously-written rows) rather than 500ing when it is off.
var logStore = new Arbitarr.Data.Logging.LogStore(
    Path.Combine(configDirectory, Arbitarr.Data.Logging.LogStore.DatabaseFileName));
logStore.EnsureCreated();
builder.Services.AddSingleton(logStore);

if (!string.Equals(
        Environment.GetEnvironmentVariable("ARBITARR_LOGDB_ENABLED"),
        "false",
        StringComparison.OrdinalIgnoreCase))
{
    // Information matches the level Sonarr registers its own database target at.
    //
    // onError goes to stderr rather than through ILogger, and that is the whole point: this
    // callback fires when the SQLite sink itself could not write, so routing it back through the
    // logging pipeline would enqueue the report of the failure into the queue that is failing.
    // The same applies to the sink's dropped-entry notice, which IS written to the log store --
    // when the store is unwritable, both the failure and the notice about it vanish, and the Logs
    // tab becomes structurally unable to explain why it is empty. Console/docker logs is the one
    // surface guaranteed to survive that, so the last-resort report goes there directly.
    //
    // arb-1of: the registration moved behind LoggingSetup.AddArbitarrSqliteLogging, which adds
    // this provider AND the per-provider category filters that keep framework chatter out of the
    // store. It is one call rather than inline statements so a test can drive the same code --
    // Program.cs's top-level statements are not independently constructible, so the guards for
    // this region have had to assert against Program.cs's TEXT, which cannot show that a filter
    // actually drops a line. Console is a different provider type and is deliberately unfiltered.
    Arbitarr.Host.Logging.LoggingSetup.AddArbitarrSqliteLogging(
        builder.Logging,
        logStore,
        onError: ex => Console.Error.WriteLine(
            $"[arbitarr] log sink write failed; entries are being dropped. {ex.GetType().Name}: {ex.Message}"));
}

builder.Services.AddSingleton(new SqliteConnectionOptions { DatabasePath = databasePath });
builder.Services.AddSingleton<SqliteConnectionFactory>();

// #56: backup and restore. BackupPaths is built from the SAME configDirectory the database and the
// secret above come from, so it can never point somewhere else than the running process does.
//
// The backup covers arbitarr.db and release-guid-secret.key and DELIBERATELY NOT the separate log
// database registered just above (LogStore.DatabaseFileName) — the two SQLite files were split so a
// configuration backup does not drag log contents along, and BackupArchiveLayout carries the full
// reasoning. Anything added later that spans "the databases" must grep for that constant.
builder.Services.AddSingleton(new Arbitarr.Data.Backup.BackupPaths(configDirectory));
builder.Services.AddSingleton<Arbitarr.Data.Backup.BackupStateStore>();
builder.Services.AddSingleton(sp => new Arbitarr.Data.Backup.BackupService(
    sp.GetRequiredService<Arbitarr.Data.Backup.BackupPaths>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new Arbitarr.Data.Backup.RestoreService(
    sp.GetRequiredService<Arbitarr.Data.Backup.BackupPaths>(),
    sp.GetRequiredService<Arbitarr.Data.Backup.BackupService>()));
builder.Services.AddSingleton(sp => new Arbitarr.Data.Backup.AutomaticBackupJob(
    sp.GetRequiredService<Arbitarr.Data.Backup.BackupPaths>(),
    sp.GetRequiredService<Arbitarr.Data.Backup.BackupService>(),
    sp.GetRequiredService<Arbitarr.Data.Backup.BackupStateStore>(),
    sp.GetRequiredService<ILogger<Arbitarr.Data.Backup.AutomaticBackupJob>>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(sp => new Arbitarr.Api.Admin.RestoreCoordinator(
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped(sp =>
{
    var factory = sp.GetRequiredService<SqliteConnectionFactory>();
    var options = ArbitarrDbContextOptionsFactory.Create(factory);
    return new ArbitarrDbContext(options);
});

builder.Services.AddSingleton(TimeProvider.System);

// Issue #46/R1: read once at startup and cache as a singleton. Assembly metadata cannot change
// during the process lifetime, so re-reading it per request (as the old inline /health version
// literal effectively forced by being hardcoded) would be pointless work.
builder.Services.AddSingleton(sp => Arbitarr.Api.SystemInfo.BuildInfo.ReadOnce(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(CircuitBreakerOptions.Default);
builder.Services.AddSingleton<SourceCircuitBreaker>();
builder.Services.AddScoped<SourceHealthRepository>();
builder.Services.AddScoped<IAsyncCircuitBreaker, PersistentSourceCircuitBreaker>();
builder.Services.AddScoped<ICapsCacheStore, CapsCacheStore>();
builder.Services.AddScoped<CapsAggregator>();

builder.Services.AddSingleton<RecentSearchLog>();
builder.Services.AddSingleton<ObservabilityCounters>();

// 15 minutes: the AC0c-measured *arr RSS sync interval used as the settings floor/ceiling anchor
// (docs/step0-measurements.md §3 — Sonarr's 15m is the more conservative of Sonarr/Radarr).
// Scoped (not singleton): EffectiveSettingsReader captures ArbitarrDbContext, itself scoped.
builder.Services.AddScoped(sp => new EffectiveSettingsReader(
    sp.GetRequiredService<ArbitarrDbContext>(),
    TimeSpan.FromMinutes(15)));

// #53 stage 53b: the environment is now *seed material only*, not a permanent input. It is read
// here (before Build(), where Configuration lives) but consulted exactly once — by SourceSeeder,
// after migrations — to populate the sources table on a first run with an empty table. Thereafter
// the database is authoritative and these values are inert. See SourceSeeder's doc comment for the
// 2026-09-07 incident that made env-as-fallback untenable.
var nzbHydraSection = builder.Configuration.GetSection("Arbitarr:Sources:NzbHydra");
var nzbHydraEnvironment = new EnvironmentSourceConfiguration(
    BaseUrl: nzbHydraSection["BaseUrl"] ?? "http://127.0.0.1:5076",
    ApiKey: nzbHydraSection["ApiKey"] ?? string.Empty,
    SourceName: nzbHydraSection["SourceName"] ?? "NZBHydra2",
    BaseUrlWasSupplied: nzbHydraSection["BaseUrl"] is not null,
    SourceNameWasSupplied: nzbHydraSection["SourceName"] is not null);
builder.Services.AddSingleton(nzbHydraEnvironment);

// Populated once at startup by SourceSeeder, after Database.Migrate() — the database cannot be read
// safely before that, which is exactly why the source configuration can no longer be baked into
// these registrations the way the pre-53b env-var wiring did.
//
// arb-x7w8.4: this no longer feeds the search path, which now resolves EVERY enabled row through
// SourceRegistry per scope. What it still answers is the dashboard's "is anything configured"
// question below, which SourceSeeder is the right place to settle because it runs once, after
// migrations, in the same pass that logs what was resolved and warns about divergence.
var resolvedSourceConfiguration = new ResolvedSourceConfiguration();
builder.Services.AddSingleton(resolvedSourceConfiguration);

// "Configured" still means AT LEAST ONE ENABLED NZBHYDRA SOURCE WITH A KEY — deliberately NOT "at
// least one enabled source of any kind". The answer is produced by SourceSeeder's resolve pass,
// whose query filters Kind == NzbHydraKind && Enabled (SourceSeeder:184), so a deployment whose only
// sources are direct Newznab/Torznab rows searches them through SourceRegistry while still reporting
// unconfigured here. Restating this predicate for N sources is arb-72mf's, not arb-x7w8.4's: it is a
// dashboard-contract change rather than a comment fix, so the behaviour was left alone.
// Registered as a factory rather than an instance because the resolution has not happened yet at
// this point in startup; the singleton is first resolved on a request, long after SourceSeeder has
// run. The dashboard's effective-config view (M2 §2, D1 surface 3) reports this without ever
// exposing a key itself.
builder.Services.AddSingleton(sp => new NzbHydraConfigurationStatus(
    IsConfigured: sp.GetRequiredService<ResolvedSourceConfiguration>().IsConfigured));

// SEC-M1 (SSRF): the source adapter validates <link> origins itself, but disabling automatic
// redirect-following here is defense in depth — an upstream response could otherwise 30x us to an
// arbitrary host and we'd fetch it before the origin check ever saw the real target.
//
// ONE NAMED REGISTRATION PER ADAPTER KIND SERVES N SOURCES, and that is sound rather than a
// shortcut: IHttpClientFactory.CreateClient returns a DISTINCT HttpClient per call (only the
// handler chain behind it is pooled), so each source SourceRegistry builds owns its own client.
// That distinctness is required, not merely tidy — both adapters assign HttpClient.Timeout from
// their row's TimeoutSeconds, so a client shared across sources would make one row's timeout win
// for all of them (NewznabSource:46-51 states the seam).
builder.Services.AddHttpClient<NzbHydraSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient(Arbitarr.Host.Sources.SourceRegistry.NewznabHttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// arb-x7w8.4: the search path's sources, resolved PER SCOPE from the enabled rows rather than from
// one configuration captured at startup — which is what makes adding, removing or disabling an
// indexer take effect without a restart.
//
// REGISTERED AS ISourceRegistry, NOT AS IReadOnlyList<IUpstreamSource>, and that shape is forced
// rather than chosen. Resolving the sources reads the database, so it is async; a list injected by
// constructor has to come from a DI factory delegate, which cannot be async — so keeping the list
// shape would mean blocking a thread-pool thread inside that factory on EVERY scope, i.e. every
// search. HostBlockingAsyncCallTests bans exactly that, for a measured reason (it starved the pool
// and surfaced as the arb-agh flake). Consumers therefore take the registry and await it at the top
// of their own work. It is SCOPED and memoises within the scope, so two consumers in one request
// share one resolution and the per-source keys are read once.
//
// arb-x7w8.10's per-indexer budgets and durable backoff decorate at THIS boundary.
builder.Services.AddScoped<Arbitarr.Host.Sources.SourceRegistry>();
builder.Services.AddScoped<ISourceRegistry>(sp =>
    sp.GetRequiredService<Arbitarr.Host.Sources.SourceRegistry>());
builder.Services.AddScoped<UpstreamMergeStage>();
builder.Services.AddScoped<IQuerySnapshotStore, QuerySnapshotStore>();

// Two-age search-result cache (M3). Everything on the read path is scoped because the EF-backed
// store shares the per-request ArbitarrDbContext; the RefreshWorker below is a singleton hosted
// service that opens its own scope per cycle rather than capturing one of these.
builder.Services.AddScoped<ISearchResultCacheStore, SearchResultCacheStore>();
builder.Services.AddScoped<SearchResultCache>();
builder.Services.AddScoped<SearchResultCacheStage>();
builder.Services.AddScoped<SearchResultRefresher>();
builder.Services.AddScoped<RefreshFetcher>(sp =>
    (_, entry, cancellationToken) => sp.GetRequiredService<SearchResultRefresher>().RefreshAsync(entry, cancellationToken));
builder.Services.AddScoped<FilterProfileLoader>();
builder.Services.AddScoped<ApiKeyProfileResolver>();
builder.Services.AddScoped<SettingsReader>();
builder.Services.AddScoped<DatabaseSizeReporter>();

// M7-8c/AC24: resolved via an explicit factory (rather than relying on DI's constructor
// selection between PaginationSnapshotService's fixed-TTL and live-TTL overloads) so the
// live-TTL ctor is always the one the Host wires up.
builder.Services.AddScoped<ISnapshotTtlSource, SettingsSnapshotTtlSource>();

// arb-b5z: which sources produced a snapshot is part of what that snapshot IS, so the resolved
// source set is a component of the snapshot token. SCOPED because it reads the scoped
// ArbitarrDbContext.
//
// arb-x7w8.4 made it LIVE rather than constant for the process. It used to hash
// ResolvedSourceConfiguration once, which was sound only while the source set could not change
// while running. SourceRegistry ended that -- a source added, removed or disabled now takes effect
// on the next request -- so a startup-frozen fingerprint would hand one source set's persisted
// snapshot rows to a different one, within a single process. It now derives from the enabled rows
// on each call.
builder.Services.AddScoped<ISourceSetFingerprintSource, Arbitarr.Host.Sources.ResolvedSourceSetFingerprintSource>();
builder.Services.AddScoped(sp => new PaginationSnapshotService(
    sp.GetRequiredService<UpstreamMergeStage>(),
    sp.GetRequiredService<SearchResultCacheStage>(),
    sp.GetRequiredService<IQuerySnapshotStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ISnapshotTtlSource>(),
    sp.GetRequiredService<ISourceSetFingerprintSource>()));

// M7-7/R20: worker-health snapshot, singleton so both the hosted RefreshWorker (writer) and
// StatusEndpoint (reader) share the same instance across the app's lifetime.
builder.Services.AddSingleton<RefreshWorkerHealthTracker>(_ => new RefreshWorkerHealthTracker(RefreshWorkerDefaults.WorkerEnabled));
builder.Services.AddSingleton<IRefreshWorkerHealth>(sp => sp.GetRequiredService<RefreshWorkerHealthTracker>());

// arb-ln0: sticky per-source download-refusal health, singleton for the same reason the worker
// health above is — the download proxy (writer) and StatusEndpoint (reader) must see one instance
// for the app's lifetime.
//
// arb-v3w: the tracker is now TWO-TIER — the in-memory holder below still owns the entry semantics
// (in particular that a repeat preserves ObservedSinceUtc), and PersistentDownloadRefusalTracker
// mirrors each write to the DownloadRefusalEntry table so the item survives a restart. The
// misconfiguration behind it does, and before this a fresh process showed a clean dashboard while
// every download still failed.
//
// The store is SCOPED (it holds the scoped ArbitarrDbContext, which is not thread-safe), so this
// singleton reaches it through a scope factory — one scope created and disposed per operation,
// exactly as PersistentReleaseLookup and ScopedEventSink do. The connection string is NEVER
// formatted here: the context's options come from DatabaseConnectionStrings, which is also what
// pins this to arbitarr.db rather than the logs database.
builder.Services.AddScoped<Arbitarr.Core.Diagnostics.IDownloadRefusalStore>(sp =>
    new Arbitarr.Data.Diagnostics.DownloadRefusalStore(sp.GetRequiredService<ArbitarrDbContext>()));

builder.Services.AddSingleton<Arbitarr.Core.Diagnostics.DownloadRefusalTracker>();
builder.Services.AddSingleton(sp => new Arbitarr.Core.Diagnostics.PersistentDownloadRefusalTracker(
    sp.GetRequiredService<Arbitarr.Core.Diagnostics.DownloadRefusalTracker>(),
    async (operation, cancellationToken) =>
    {
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        await operation(
                scope.ServiceProvider.GetRequiredService<Arbitarr.Core.Diagnostics.IDownloadRefusalStore>(),
                cancellationToken)
            .ConfigureAwait(false);
    },
    sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Arbitarr.Core.Diagnostics.PersistentDownloadRefusalTracker).FullName!)));

// arb-apj: what the rest of the app resolves is that state holder WRAPPED in a transition observer,
// so one notification goes out when a source's health item appears and one when it clears — and
// nothing at all while it merely persists, which is the whole requirement.
//
// A decorator rather than a callback inside DownloadRefusalTracker: the tracker stays a plain state
// holder with no notification concept in it, and arb-v3w (which replaced its backing store) touched
// no line this feature owns — swapping the inner registration is all that change needed.
//
// DownloadRefusalNotifier holds the scope FACTORY, not a scope: this fires from the download proxy's
// request path, whose scope is gone by the time the delivery completes. The callback returns
// immediately and swallows its own failures, because a misconfigured webhook must never turn a
// refused download's clean 502 into a 500 (§3.4/AC6).
builder.Services.AddSingleton<Arbitarr.Host.Notifications.DownloadRefusalNotifier>(sp =>
    new Arbitarr.Host.Notifications.DownloadRefusalNotifier(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<Arbitarr.Host.Notifications.DownloadRefusalNotifier>>()));

builder.Services.AddSingleton<Arbitarr.Core.Diagnostics.IDownloadRefusalTracker>(sp =>
    new Arbitarr.Core.Diagnostics.NotifyingDownloadRefusalTracker(
        sp.GetRequiredService<Arbitarr.Core.Diagnostics.PersistentDownloadRefusalTracker>(),
        sp.GetRequiredService<Arbitarr.Host.Notifications.DownloadRefusalNotifier>().NotifyInBackground));

// arb-v3w: loads the persisted refusals into the tracker once at startup. See the service's own doc
// for why this is a hosted one-shot rather than a lazy first read.
//
// arb-pu58: the same pass now prunes rows whose source no longer exists, so it also takes the scope
// factory — it reads the Sources table (scoped DbContext) to decide what "no longer exists" means.
builder.Services.AddHostedService(sp => new Arbitarr.Host.Diagnostics.DownloadRefusalRehydrationService(
    sp.GetRequiredService<Arbitarr.Core.Diagnostics.PersistentDownloadRefusalTracker>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<Arbitarr.Host.Diagnostics.DownloadRefusalRehydrationService>>()));

// M7-8b/AC24: options are re-read from the settings store on every cycle (see
// SettingsRefreshWorkerOptionsSource), not captured once at startup from RefreshWorkerDefaults.
builder.Services.AddScoped<IRefreshWorkerOptionsSource, SettingsRefreshWorkerOptionsSource>();

// #53 stage 53b: the worker's source name now comes from the resolved (database-backed) source
// rather than straight from configuration, so it stays consistent with the name the IUpstreamSource
// above reports — the circuit breaker is keyed by this name, so a mismatch would silently split one
// source's breaker state in two.
//
// ORDERING: this factory runs when the host starts its hosted services, which is *after* the
// SourceSeeder call below (both happen before app.Run(), and the seeder runs earlier in the
// startup sequence than IHostedService.StartAsync). RefreshWorker captures the name in its
// constructor, so it must not be resolved any earlier than this. The literal fallback covers a
// deployment with no source configured at all.
builder.Services.AddHostedService(sp => new RefreshWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ResolvedSourceConfiguration>().SourceName ?? "NZBHydra2",
    logger: sp.GetRequiredService<ILogger<RefreshWorker>>(),
    health: sp.GetRequiredService<IRefreshWorkerHealth>(),
    // #55 step 2: the worker records what each cycle actually did, and any source failure it hit.
    eventSink: sp.GetRequiredService<IEventSink>()));

// AI layer (M5, Step 6): Arbitarr.Ai has zero references to Arbitarr.Data/Arbitarr.Media (AC6a,
// enforced by Arbitarr.Architecture.Tests.AiMediaIsolationTests/DependencyDirectionTests) — Host
// is the sole place that composes it with its persistence-backed cache reader/writer and the
// shared circuit breaker (keyed by source name "Ollama", same IAsyncCircuitBreaker instance every
// other adapter uses). Base URL defaults to the in-cluster service name, never a LAN IP; tests use
// http://ollama.example.invalid.
//
// #89/#112: OllamaOptions carries only STARTUP FALLBACKS, not the live values. BaseUrl and Model
// are both settings rows now (OllamaBaseUrl, OllamaModel), resolved per call through their
// resolvers so a change on the Settings page takes effect without a restart. This singleton still
// carries both because KeepAlive has no settings surface (deliberately out of #112's scope, and
// nothing about it is per-operator), and — the load-bearing reason — because an OllamaClient
// constructed WITHOUT resolvers must still work: that is the shape every existing test constructs,
// and the honest behaviour for a caller with no settings store behind it.
builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Arbitarr:Ai:Ollama");
    var baseUrlRaw = section["BaseUrl"] ?? Arbitarr.Data.Settings.OllamaBaseUrlResolver.DefaultBaseUrl;
    var model = section["Model"] ?? Arbitarr.Data.Settings.OllamaModelResolver.DefaultModel;
    var keepAliveRaw = section["KeepAlive"];
    // arb-43b: the accepted forms and the wire shape are OllamaKeepAlive's, in Core, so this host
    // block no longer carries its own copy of the duration pattern. A null/absent value falls
    // through the same path as a malformed one and lands on the default, but without the warning:
    // not configuring KeepAlive is normal, whereas configuring it wrongly is what an operator needs
    // to be told about.
    if (!Arbitarr.Core.Ai.OllamaKeepAlive.TryParse(keepAliveRaw, out var keepAlive) && keepAliveRaw is not null)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Arbitarr.Host.Program");
        logger.LogWarning(
            "AI: the Arbitarr:Ai:Ollama:KeepAlive environment variable is not a usable value " +
            "(expected a bare integer or a Go duration string with a unit, e.g. \"-1\" or \"30m\") " +
            "and was not used for the startup-fallback client options; the built-in default (\"-1\") " +
            "was used instead.");
        // No reassignment: TryParse already yields OllamaKeepAlive.Default on failure, which IS the
        // "-1" this warning promises. Setting it again here would be a second place for the two to
        // disagree.
    }

    // arb-6u6: this singleton's BaseUrl is only ever the STARTUP FALLBACK (see the comment above
    // for why it must still work standalone) — it is never the live value once OllamaBaseUrlSeeder
    // has run, so it must survive the same malformed-env-value case the seeder already handles
    // rather than crashing the whole host with new Uri(...) before the seeder gets a chance to
    // seed the working default. Reuses SettingsValidator.ValidateOllamaBaseUrl — the same check
    // every write path already runs — rather than a second, possibly-divergent parse rule.
    var baseUrl = baseUrlRaw;
    try
    {
        Arbitarr.Core.Settings.SettingsValidator.ValidateOllamaBaseUrl(baseUrlRaw);
    }
    catch (Arbitarr.Core.Settings.SettingsValidationException)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Arbitarr.Host.Program");
        logger.LogWarning(
            "AI: the Arbitarr:Ai:Ollama:BaseUrl environment variable is not a usable Ollama address " +
            "and was not used for the startup-fallback client options; the built-in default ({BaseUrl}) " +
            "was used instead. The rejected value is deliberately not shown here because it may " +
            "contain a credential.",
            Arbitarr.Data.Settings.OllamaBaseUrlResolver.DefaultBaseUrl);
        baseUrl = Arbitarr.Data.Settings.OllamaBaseUrlResolver.DefaultBaseUrl;
    }

    return new OllamaOptions(new Uri(baseUrl), model, keepAlive);
});

// #89/#112: the process-wide caches of the base URL and model in force. Singletons because they
// must outlive a request — the whole point is that a write in one request is seen by the next. The
// settings write path invalidates them; the resolvers (scoped, they need the DbContext) repopulate.
builder.Services.AddSingleton<Arbitarr.Core.Ai.OllamaBaseUrlCache>();
builder.Services.AddScoped<Arbitarr.Data.Settings.OllamaBaseUrlResolver>();
builder.Services.AddSingleton<Arbitarr.Core.Ai.OllamaModelCache>();
builder.Services.AddScoped<Arbitarr.Data.Settings.OllamaModelResolver>();

// #112: SCOPED, where it was a singleton before. AiModelIdentity keys the verdict cache (R17: a
// model change must invalidate previously cached verdicts), so it has to FOLLOW the resolved model
// rather than the start-up one — a singleton would have kept serving the boot-time name after an
// operator picked a different model, and every cached verdict would have stayed keyed to a model
// that was no longer being asked. Scoped is the narrowest lifetime that lets it re-read: it is
// consumed by FilterStage (already scoped) and by ClassifierPollingWorker, which resolves it from
// the per-cycle scope it already creates.
//
// The read is synchronous because a DI factory has no await; OllamaModelResolver.Get() documents
// why that is a real sync query rather than a blocking wait on the async one. It is served from the
// singleton cache on all but the first call after a write.
builder.Services.AddScoped(sp =>
{
    var section = builder.Configuration.GetSection("Arbitarr:Ai");
    // Arbitarr:Ai:ModelName remains an explicit override for a deployment that needs the cache key
    // pinned independently of the model actually being called. Unset everywhere, and left in place
    // rather than removed because removing it is not #112's question.
    var modelName = section["ModelName"]
        ?? sp.GetRequiredService<Arbitarr.Data.Settings.OllamaModelResolver>().Get();
    var modelDigest = section["ModelDigest"] ?? "unknown";
    // arb-p4r: bumped from "v1" to "v2". Pinning options.temperature/seed on the Ollama request
    // (OllamaClient) changes what a given prompt yields for the same input, so verdicts cached
    // under the old, non-deterministic sampling must not be served as if they came from the new,
    // deterministic one.
    //
    // arb-qg3o: the by-construction fix has since landed, and it closed the operator-override hole
    // this comment used to warn about — sampling now travels in its own DecodingIdentity component
    // below, which reads no configuration, so pinning Arbitarr:Ai:PromptVersion can no longer keep
    // verdicts cached under old sampling alive. The default stays at "v2" deliberately: reverting
    // it to "v1" would itself invalidate the whole cache a second time for no benefit.
    //
    // arb-458f: bumped "v2" -> "v3". This one IS the term's documented meaning (CONTEXT.md: the
    // version tag of the prompt TEMPLATE, and only the template) rather than arb-p4r's stretched
    // use of it — the user message now carries the Usenet poster/group/files/password/grabs lines,
    // so the same release asks the model a materially different question than it did under "v2"
    // and verdicts cached against the old template must not be served for the new one.
    var promptVersion = section["PromptVersion"] ?? "v3";
    // Built in Arbitarr.Ai from the sampling constants and passed to Core as a plain string, so
    // Core acquires no Ollama types (ADR-0001).
    return new AiModelIdentity(modelName, modelDigest, promptVersion, OllamaOptions.DecodingIdentity);
});
// SEC-M5 (SSRF): mirrors SEC-M1 above — the Ollama base URL is config-driven, but disabling
// automatic redirect-following is defense in depth against a compromised/misconfigured endpoint
// 30x-ing us to an arbitrary host before any origin check could see the real target.
builder.Services.AddHttpClient(nameof(OllamaClient))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IOllamaClient>(sp =>
{
    var options = sp.GetRequiredService<OllamaOptions>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient(nameof(OllamaClient));
    var circuitBreaker = sp.GetRequiredService<IAsyncCircuitBreaker>();
    // #89: the base URL is resolved PER CALL, from the database, so an operator changing it on the
    // Settings page does not have to restart the host. The resolver is captured from THIS scope,
    // which is correct because IOllamaClient is itself scoped; the cache behind it is the singleton
    // that carries a write across scopes.
    var resolver = sp.GetRequiredService<Arbitarr.Data.Settings.OllamaBaseUrlResolver>();
    // #112: and the model likewise, so an operator picking a different model from the instance's
    // own list has the NEXT classification use it.
    var modelResolver = sp.GetRequiredService<Arbitarr.Data.Settings.OllamaModelResolver>();
    return new OllamaClient(
        options,
        httpClient,
        circuitBreaker,
        async ct => new Uri(await resolver.GetAsync(ct)),
        async ct => await modelResolver.GetAsync(ct));
});
builder.Services.AddScoped<ReleaseClassifier>();
builder.Services.AddScoped<IVerdictCacheReader, VerdictCacheReader>();
builder.Services.AddScoped<IVerdictCacheWriter, VerdictCacheWriter>();
builder.Services.AddScoped(sp => new ClassifierWorker(
    sp.GetRequiredService<ReleaseClassifier>(),
    sp.GetRequiredService<IVerdictCacheWriter>(),
    sp.GetRequiredService<AiModelIdentity>(),
    sp.GetRequiredService<ObservabilityCounters>()));

// AC14b: the ad-hoc search endpoint's synchronous-AI opt-in. Registered here (Host, sole
// composition root) against Arbitarr.Core.Arbitration.ISyncReleaseArbiter so Arbitarr.Api never
// references Arbitarr.Ai (AC6a). Strictly separate from the Q1-B machine path above, which never
// resolves this or any other inline-classification type.
builder.Services.AddScoped<Arbitarr.Core.Arbitration.ISyncReleaseArbiter, SyncReleaseArbiter>();

builder.Services.AddScoped<FilterStage>(sp => new FilterStage(
    sp.GetRequiredService<ApiKeyProfileResolver>(),
    sp.GetRequiredService<SettingsReader>(),
    sp.GetRequiredService<ArbitarrDbContext>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IVerdictCacheReader>(),
    sp.GetRequiredService<AiModelIdentity>(),
    sp.GetRequiredService<ObservabilityCounters>(),
    // #55 step 2: one Decision event per suppression, alongside (never instead of) the append-only
    // suppression audit log this stage already writes. These are the rows #54 hangs its verdict on.
    sp.GetRequiredService<IEventSink>()));

builder.Services.AddSingleton<InMemoryReleaseLookup>();

// ClassifierPollingWorker is the BackgroundService that drives ClassifierWorker (a plain Scoped
// type): it snapshots InMemoryReleaseLookup each cycle and runs classification + (AC26b/R17)
// worker-side title-rewrite caching. AC24: the poll interval is
// re-read from settings at the top of every cycle via the scope factory, so a live settings change
// takes effect without a restart.
// #112: AiModelIdentity is no longer passed here — it is scoped now and the worker resolves it
// from the per-cycle scope, so a model change on the Settings page reaches the verdict cache key
// on the next cycle instead of at the next restart. See that constructor's doc.
builder.Services.AddHostedService(sp => new ClassifierPollingWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<InMemoryReleaseLookup>(),
    sp.GetRequiredService<TimeProvider>(),
    logger: sp.GetRequiredService<ILogger<ClassifierPollingWorker>>()));
// arb-tps: the durable tier behind the in-memory one. Scoped, because it holds the scoped
// ArbitarrDbContext; the search routes are scoped and take it directly, while the singleton
// IReleaseLookup below reaches it through a scope factory rather than capturing one.
//
// The TTL is read per WRITE from SettingsReader rather than baked in at startup, so an operator
// lowering it still does not need a restart. arb-zwk: it used to be read per CONSTRUCTION, which on
// a scoped registration meant a blocking GetAwaiter().GetResult() on a thread-pool thread for every
// scope — i.e. every search — to fetch a value only the upsert path uses. Handing the store the
// reader keeps the freshness and lets the read be awaited where it is actually needed. The
// connection string is NEVER formatted here — the context's options come from
// DatabaseConnectionStrings (data.md:59).
builder.Services.AddScoped<IReleaseLookupStore>(sp =>
{
    var settingsReader = sp.GetRequiredService<SettingsReader>();
    return new ReleaseLookupStore(
        sp.GetRequiredService<ArbitarrDbContext>(),
        settingsReader.GetReleaseLookupTtlAsync,
        sp.GetRequiredService<TimeProvider>());
});

// arb-tps: IReleaseLookup is now the TWO-TIER lookup — memory first, the store on a miss, memory
// repopulated from a store hit. This is what makes a download link survive a restart and outlive
// the in-memory tier's 30-minute TTL; before it, every link did neither.
//
// The InMemoryReleaseLookup singleton registration above is KEPT exactly as it was, and so is
// ClassifierPollingWorker's use of its Snapshot(): that worker classifies what this process has
// recently rendered, which is an in-memory question, not a database one.
builder.Services.AddSingleton<IReleaseLookup>(sp => new PersistentReleaseLookup(
    sp.GetRequiredService<InMemoryReleaseLookup>(),
    async (proxyGuid, cancellationToken) =>
    {
        // A scope per lookup, created and disposed around the query: this singleton outlives every
        // request scope, so resolving the scoped store once would hand every later download the
        // first request's DbContext.
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IReleaseLookupStore>()
            .FindAsync(proxyGuid, cancellationToken)
            .ConfigureAwait(false);
    },
    // arb-zwk: so a store fault on the download path is attributable instead of silently answering
    // as a miss — the root cause of the arb-agh flake.
    sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PersistentReleaseLookup).FullName!)));

// Inbound Torznab/Newznab client apikey (M1-9, security-hardened). Distinct from
// Arbitarr:Sources:NzbHydra:ApiKey (the upstream NZBHydra2 credential Arbitarr uses to call out)
// and from SettingKey.AdminApiKey (a separate M4/M7 concept). "Arbitarr:ClientApiKeys:<n>:Name"/
// "...:Key" configures named keys; a single legacy "Arbitarr:ApiKey" value collapses to one named
// key, "default", for backward compatibility.
builder.Services.AddSingleton(_ =>
{
    var namedKeys = builder.Configuration
        .GetSection("Arbitarr:ClientApiKeys")
        .Get<NamedClientApiKey[]>() ?? Array.Empty<NamedClientApiKey>();

    var legacyKey = builder.Configuration["Arbitarr:ApiKey"];
    var keys = namedKeys.Length > 0
        ? namedKeys
        : string.IsNullOrEmpty(legacyKey)
            ? Array.Empty<NamedClientApiKey>()
            : new[] { new NamedClientApiKey("default", legacyKey) };

    return new ConfiguredClientApiKeyResolver(keys);
});

// #97: what the search routes resolve is the DB-backed composite, NOT the config resolver above --
// that one is registered as its concrete type and is now the composite's environment half. A key
// minted in Settings > API keys must open /torznab/api, /newznab/api and /download/{proxyGuid}, or
// the API keys section is telling the operator something false. See DbClientApiKeyResolver for the
// ordering (minted first) and for why the environment keys keep working across the upgrade.
//
// Singleton, resolving its own scope per call: the endpoints take IClientApiKeyResolver from the
// root provider, and ApiKeyRepository wraps the scoped ArbitarrDbContext, which is not thread-safe.
builder.Services.AddSingleton<IClientApiKeyResolver>(sp => new DbClientApiKeyResolver(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ConfiguredClientApiKeyResolver>(),
    sp.GetRequiredService<IApiKeyLastUsedRecorder>()));

// D2 admin API key gate (M7-6): reads SettingKey.AdminApiKey from the settings store, distinct
// from the Torznab/Newznab client apikey resolved above. Scoped: captures the scoped ArbitarrDbContext.
builder.Services.AddScoped<IAdminApiKeyReader, DbAdminApiKeyReader>();
// arb-lan-passthrough (ADR 0012): default-on LAN admin passthrough, off via ARBITARR_LAN_PASSTHROUGH=false.
builder.Services.AddSingleton(Arbitarr.Api.Security.LanPassthroughOptions.FromEnvironment());
builder.Services.AddScoped<AdminApiKeyFilter>();

// #58: named, scoped API keys. DbCredentialResolver — not IAdminApiKeyReader — is now what the gate
// consults; the reader above survives as ONE OF ITS TWO INPUTS, because the pre-#58 shared key must
// keep working across the upgrade (issue §Migration) and is still written by AdminSecurityEndpoints.
// Deleting the reader would break every existing deployment on restart.
builder.Services.AddScoped(sp => new ApiKeyRepository(
    sp.GetRequiredService<ArbitarrDbContext>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ICredentialResolver, DbCredentialResolver>();

// Singleton because the throttle state it holds must outlive a request — that is the whole
// mechanism (see the type doc). It resolves its own scope per write, like ScopedEventSink, because
// ApiKeyRepository wraps the scoped ArbitarrDbContext, which is not thread-safe.
builder.Services.AddSingleton<IApiKeyLastUsedRecorder, ThrottledApiKeyLastUsedRecorder>();

// arb-acy9: the SAME singleton, also as a hosted service, so its StopAsync drains writes that are
// still in flight when the host stops. Without it a write dispatched just before shutdown finds its
// scope's provider gone and logs the recorder's failure Warning on every restart, which buries a
// real signal in noise. GetRequiredService, not a second construction: two instances would each
// drain a set the other never wrote to.
builder.Services.AddHostedService(sp =>
    (ThrottledApiKeyLastUsedRecorder)sp.GetRequiredService<IApiKeyLastUsedRecorder>());

// #44: human authentication. Sessions authorize against #58's primitive above rather than a second
// model — DbSessionAuthenticator returns the same CredentialResolution DbCredentialResolver does, and
// AdminApiKeyFilter makes one scope check over whichever credential answered. Key authentication is
// NOT replaced: machine callers cannot complete an interactive login.
// #44: the KDF cost is a composition-root decision. ASP.NET's default is 100,000 iterations,
// below OWASP's current figure for PBKDF2-HMAC-SHA256, so it is configured explicitly here rather
// than inherited. Raising it is not a migration: the v3 hash format embeds the count, so existing
// rows keep verifying at their own cost (AspNetPasswordHasher.Verify treats SuccessRehashNeeded as
// success, and a test exercises that branch against a hash made at the old count).
builder.Services.Configure<PasswordHasherOptions>(
    options => options.IterationCount = AspNetPasswordHasher.IterationCount);
builder.Services.AddScoped<IPasswordHasher, AspNetPasswordHasher>();
builder.Services.AddScoped(sp => new UserRepository(
    sp.GetRequiredService<ArbitarrDbContext>(),
    sp.GetRequiredService<IPasswordHasher>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped(sp => new SessionRepository(
    sp.GetRequiredService<ArbitarrDbContext>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<ISessionAuthenticator, DbSessionAuthenticator>();

// Singleton for the same reason as the recorder above: the throttle state must outlive a request.
builder.Services.AddSingleton<ISessionActivityRecorder, ThrottledSessionActivityRecorder>();

// arb-acy9: and the same drain for sessions, for the same reason — with a sharper consequence,
// since a lost stamp here can idle out a live session rather than stale a display.
builder.Services.AddHostedService(sp =>
    (ThrottledSessionActivityRecorder)sp.GetRequiredService<ISessionActivityRecorder>());

// Singleton because the rate-limit counters must outlive a request — a per-request limiter would
// count to one forever and defend against nothing.
builder.Services.AddSingleton(sp => new LoginRateLimiter(sp.GetRequiredService<TimeProvider>()));

// M7-5 settings write path: shares the same measured *arr RSS sync interval as EffectiveSettingsReader
// above, so read and write validation agree on cross-field bounds (e.g. FreshUntilCeiling).
builder.Services.AddScoped(sp => new SettingsRepository(
    sp.GetRequiredService<ArbitarrDbContext>(),
    TimeSpan.FromMinutes(15)));

// #53: source persistence. Introduced unread in 53a; since 53b it backs the seed-once-then-DB
// resolution path (SourceSeeder, plan §3.2's OWNER RULING), and since 53c it also backs the
// admin CRUD surface (AdminSourceEndpoints).
builder.Services.AddScoped<SourceRepository>();

// arb-x7w8.3: the SINGLE production reader of a stored per-source API key (ADR 0018). The admin
// connectivity probe takes a SourceCredential from this type rather than reading the key itself,
// and the direct-indexer search path will be its second consumer -- which is the whole point:
// SourceRepository.ReadApiKeyForUpstreamRequestAsync stays at exactly one call site, which is the
// form that guarantee takes (CLAUDE.md section 1, docs/standards/architecture.md). Same shape, and
// same reason, as SonarrCredentialProvider below.
//
// arb-x7w8.4 IS that second consumer: SourceRegistry takes a SourceCredential per enabled row and
// never touches the repository's reader. The count is unchanged.
builder.Services.AddScoped<Arbitarr.Data.Sources.SourceCredentialProvider>();


// #53 stage 53c: the §3.3 connectivity test's HTTP client. AllowAutoRedirect is disabled for the
// same SSRF reason as the NzbHydraSource client above — a probed source could otherwise 30x us to
// an arbitrary host and we would issue the request (carrying that source's API key) before anything
// checked the target. Redirects off means the probe reports the 3xx as an unexpected response
// instead, which is the truthful answer for a source that is not where it says it is.
builder.Services.AddHttpClient<SourceConnectivityProber>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// arb-u1c: the Sonarr instance's configuration (base URL + write-only API key), stored as two
// colon-namespaced rows in the existing Settings table -- NO NEW TABLE, so there is nothing new for
// MaintenanceJob to prune (the rows are fixed in number and do not accumulate). The key is stored
// write-only under a name no SettingKey can produce, exactly as source API keys and the webhook URL
// are, so it can never surface through GET /api/admin/settings.
builder.Services.AddScoped<Arbitarr.Data.Media.ArrInstanceRepository>();

// arb-u1c: the Sonarr connectivity probe. AllowAutoRedirect is disabled for the same SSRF reason as
// the three clients above -- a misconfigured address answering 30x must not make this process issue
// a request (carrying Sonarr's API key) at a host nobody configured.
//
// NO .RemoveAllLoggers() HERE, DELIBERATELY, and the reason was MEASURED rather than assumed --
// because the webhook client below DOES need it. IHttpClientFactory's logging handler writes the
// request URI at Information, which since #65 lands in the persistent log store served at
// GET /api/admin/logs. The question is where this client's key rides: it rides in the QUERY STRING
// (SonarrConnectivityProber.BuildStatusUri and ArrApiProvider.BuildEpisodeUri both put it there, the
// same placement NzbHydraSource's own load-bearing comment pins), not in the URL PATH as a webhook
// token does.
//
// TWO LAYERS COVER THAT, AND THE ONE THAT ACTUALLY FIRES HERE IS NOT THE ONE THE NEIGHBOURING
// COMMENTS NAME. .NET's own logging handler collapses the whole query string to "?*" before the
// message is formatted, so through this client the key never reaches LogMessageCleanser at all --
// the stored row reads ".../api/v3/system/status?*". The cleanser (which scrubs query-string
// credentials but NOT URL paths, CLAUDE.md §1) stays the guard for every OTHER way a key-bearing URI
// can reach a log line: an exception message, or a hand-written one.
// SonarrKeyIsScrubbedFromLogsTests drives THIS registered client through the real probe route and
// asserts both layers, with a positive control matching what is genuinely logged so it cannot pass
// vacuously. If either URI builder is ever changed to put the key in a path segment, NEITHER layer
// covers it and this registration needs .RemoveAllLoggers().
//
// THIS ALSO DEPENDS ON A PROCESS-WIDE SWITCH THIS REPOSITORY DOES NOT SET. The "?*" collapse above is
// gated by the System.Net.Http.DisableUriRedaction AppContext switch (name inverted from what it
// sounds like: setting it to true DISABLES the redaction, i.e. restores the full query string,
// key included, to the log line). Nothing here sets it, so the default (false, redaction ON) is what
// this registration's safety rests on. DisableUriRedactionSwitchTests proves both states — the
// default redacting the key, and the switch flipped defeating that redaction with the same handler —
// so this dependency is executable rather than folklore. If anything ever sets this switch true
// process-wide (a host default, a runtimeconfig.json entry, a future dependency), this registration
// needs .RemoveAllLoggers() regardless of where the key sits in the URI.
builder.Services.AddHttpClient<Arbitarr.Core.Media.SonarrConnectivityProber>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// arb-u1c: the SINGLE production reader of the stored Sonarr API key. Both the admin connectivity
// probe and the search path's identity resolver need an authenticated request against the
// configured Sonarr; routing both through this one type is what keeps
// ArrInstanceRepository.ReadApiKeyForUpstreamRequestAsync at exactly one call site, which is the
// form that guarantee takes (CLAUDE.md section 1, docs/standards/architecture.md). It lives in
// Arbitarr.Data because both Arbitarr.Api and Arbitarr.Media already reference that project and
// neither may reference the other.
builder.Services.AddScoped<Arbitarr.Data.Media.SonarrCredentialProvider>();

// arb-6l9b.1: the Radarr instance's configuration (base URL + write-only API key), stored as two
// colon-namespaced rows in the existing Settings table -- arr:radarr:base_url and arr:radarr:api_key.
// Same shape and same mechanism as the Sonarr pair above: NO NEW TABLE (the rows are fixed in number
// and do not accumulate, so there is nothing new for MaintenanceJob to prune), and the key is stored
// under a name no SettingKey can produce, so it can never surface through GET /api/admin/settings.
// NEVER add either row to SettingsCatalog -- that unreachability is the mechanism, not a coincidence.
//
// A SEPARATE TYPE RATHER THAN A GENERALISED ONE (arb-arrq D3): the rejected alternative was
// parameterising ArrInstanceRepository over an instance kind, and the grounds for rejecting it are
// recorded in RadarrInstanceRepository's own type doc so a third *arr reopens it deliberately.
// Note it takes NO IArrInstanceEpoch: that epoch is Sonarr's (it evicts SeriesTitleResolver's memo),
// Radarr has no memo keyed on it, and bumping it for a Radarr write would evict a correct Sonarr
// cache for an unrelated write.
builder.Services.AddScoped<Arbitarr.Data.Media.RadarrInstanceRepository>();

// arb-6l9b.1: the Radarr connectivity probe. AllowAutoRedirect is disabled for the same SSRF reason
// as every *arr client above -- a misconfigured address answering 30x must not make this process
// reissue a request (carrying Radarr's API key) at a host nobody configured.
//
// NO .RemoveAllLoggers() HERE, DELIBERATELY, for exactly the measured reason the
// SonarrConnectivityProber registration above sets out at length -- read that comment rather than a
// summary of it. In short: this client's key rides in the QUERY STRING
// (RadarrConnectivityProber.BuildStatusUri puts it there, matching SonarrConnectivityProber and
// ArrApiProvider), and .NET's logging handler collapses the whole query string to "?*" before the
// message is formatted, so the key never reaches the log store. RemoveAllLoggers() is needed only if
// a key ever moves into a URL PATH segment, which LogMessageCleanser does NOT scrub (CLAUDE.md
// section 1) -- it does not here. Do not move this key to an X-Api-Key header either: that would
// split the codebase's one placement convention and invalidate the comment above, which
// SonarrKeyIsScrubbedFromLogsTests and DisableUriRedactionSwitchTests pin. The same process-wide
// System.Net.Http.DisableUriRedaction dependency the Sonarr comment documents applies to this client
// unchanged.
builder.Services.AddHttpClient<Arbitarr.Core.Media.RadarrConnectivityProber>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// arb-6l9b.1: the SINGLE production reader of the stored Radarr API key, holding
// RadarrInstanceRepository.ReadApiKeyForUpstreamRequestAsync at exactly one call site -- the form
// that guarantee takes (CLAUDE.md section 1, docs/standards/architecture.md), and the same one
// SonarrCredentialProvider above exists to preserve for Sonarr's reader. Introduced with the FIRST
// consumer rather than at the second, because Sonarr's history is that the obvious wiring at the
// second consumer produces two callers, which is precisely the count the guarantee is made of. It
// lives in Arbitarr.Data so every consumer can reach it without Arbitarr.Api referencing
// Arbitarr.Media (ADR 0001).
builder.Services.AddScoped<Arbitarr.Data.Media.RadarrCredentialProvider>();

// arb-6l9b.3: the two queue readers behind GET /api/admin/arr/{sonarr,radarr}/queue. TWO typed
// clients rather than one shared instance even though the behaviour lives on a shared base
// (ArrQueueReader): each kind gets its own primary handler, its own timeout, and -- the part that is
// load-bearing for the tests -- its own LOGGER CATEGORY, which is how the key-scrubbing tests find
// the rows a given client's requests produced and assert per row that the key is absent from them.
//
// AllowAutoRedirect is disabled for the same SSRF reason as every *arr client above, and it matters
// on a read path exactly as much as on a probe: a misconfigured address answering 30x must not make
// this process reissue a request CARRYING THE INSTANCE'S API KEY at a host nobody configured. With
// redirects off the 3xx comes back as a non-success status the reader classifies as
// UnexpectedResponse -- which is also the truthful answer for an address pointing at a proxy or a
// login redirect. ArrQueueReaderTests asserts both the status and that exactly ONE request is issued.
//
// THE TIMEOUT IS SET HERE, ONCE, and ArrQueueReader must never assign HttpClient.Timeout itself: the
// client is POOLED through IHttpClientFactory, and HttpClient throws on that assignment once a
// request has started on the instance -- two concurrent reads are enough to produce it, which is the
// failure ArrApiProvider's remarks record happening. A caller wanting a shorter bound uses a linked
// CancellationTokenSource, which is exactly what ArrQueueReader.ReadQueueAsync does per call.
//
// NO .RemoveAllLoggers() ON EITHER, DELIBERATELY, for exactly the measured reason the
// SonarrConnectivityProber registration above sets out at length -- read that comment rather than a
// summary of it. In short: these clients' keys ride in the QUERY STRING
// (ArrQueueReader.BuildQueueUri puts them there, matching both connectivity probers and
// ArrApiProvider), and .NET's logging handler collapses the whole query string to "?*" before the
// message is formatted, so the key never reaches the log store. RemoveAllLoggers() would be needed
// only if a key ever moved into a URL PATH segment, which LogMessageCleanser does NOT scrub
// (CLAUDE.md section 1) -- it does not here. Do not move these keys to an X-Api-Key header either:
// that would split the codebase's one placement convention and invalidate the comment above. The
// same process-wide System.Net.Http.DisableUriRedaction dependency the Sonarr comment documents
// applies to both clients unchanged.
builder.Services.AddHttpClient<Arbitarr.Core.Media.SonarrQueueClient>(
        client => client.Timeout = Arbitarr.Core.Media.ArrQueueReader.DefaultTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddHttpClient<Arbitarr.Core.Media.RadarrQueueClient>(
        client => client.Timeout = Arbitarr.Core.Media.ArrQueueReader.DefaultTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// arb-6l9b.4: the two library readers behind GET /api/admin/arr/sonarr/series and
// /api/admin/arr/radarr/movies. TWO typed clients rather than one, for exactly the reasons the queue
// registrations above state: each kind gets its own primary handler, its own timeout, and -- the part
// that is load-bearing for the tests -- its own LOGGER CATEGORY, which is how
// ArrLibraryKeyIsScrubbedFromLogsTests finds the rows a given client's requests produced and asserts
// per row that the key is absent from them.
//
// AllowAutoRedirect is disabled for the same SSRF reason as every *arr client above: a misconfigured
// address answering 30x must not make this process reissue a request CARRYING THE INSTANCE'S API KEY
// at a host nobody configured. With redirects off the 3xx comes back as a non-success status the
// reader classifies as UnexpectedResponse. AdminArrLibraryEndpointsTests asserts both the status and
// that exactly ONE request is issued.
//
// THE TIMEOUT IS SET HERE, ONCE, and ArrLibraryReader must never assign HttpClient.Timeout itself:
// the client is POOLED through IHttpClientFactory, and HttpClient throws on that assignment once a
// request has started on the instance -- two concurrent reads are enough to produce it, which is the
// failure ArrApiProvider's remarks record happening. A caller wanting a shorter bound uses a linked
// CancellationTokenSource, which is what ArrLibraryReader.ReadLibraryAsync does per call. The bound
// is LONGER than the queue clients' because the work is different: these fetch a WHOLE library (the
// upstream endpoints are unpaged), not one page of active downloads.
//
// NO .RemoveAllLoggers() ON EITHER, DELIBERATELY, for exactly the measured reason the
// SonarrConnectivityProber registration above sets out at length -- read that comment rather than a
// summary of it. In short: these clients' keys ride in the QUERY STRING
// (ArrLibraryReader.BuildLibraryUri puts them there, matching the queue readers, both connectivity
// probers and ArrApiProvider), and .NET's logging handler collapses the whole query string to "?*"
// before the message is formatted, so the key never reaches the log store. RemoveAllLoggers() would
// be needed only if a key ever moved into a URL PATH segment, which LogMessageCleanser does NOT scrub
// (CLAUDE.md section 1) -- it does not here. Do not move these keys to an X-Api-Key header either:
// that would split the codebase's one placement convention and invalidate the comment above. The same
// process-wide System.Net.Http.DisableUriRedaction dependency the Sonarr comment documents applies to
// both clients unchanged.
builder.Services.AddHttpClient<Arbitarr.Core.Media.SonarrLibraryClient>(
        client => client.Timeout = Arbitarr.Core.Media.ArrLibraryReader<Arbitarr.Core.Media.ArrSeriesItem>.DefaultTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddHttpClient<Arbitarr.Core.Media.RadarrLibraryClient>(
        client => client.Timeout = Arbitarr.Core.Media.ArrLibraryReader<Arbitarr.Core.Media.ArrMovieItem>.DefaultTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// arb-u1c: the identity resolver that turns Sonarr's tvdbid into the series title the search path
// sends upstream. Registered against the Core.Identity contract, so Arbitarr.Api (which builds the
// search query) never sees Arbitarr.Media -- this composition root is the only place that knows
// which implementation is in play (ADR 0001).
//
// Registered UNCONDITIONALLY even though it is useless without a configured Sonarr, because the
// configuration lives in the database and can be written at any time from the admin UI. A
// registration gated on a startup-time read would leave the resolver permanently absent for anyone
// who configures Sonarr after boot -- which is everyone, on a first run. SeriesTitleResolver reads
// the rows per call and returns null throughout when they are missing, so an unconfigured instance
// costs one settings read and changes no behaviour.
builder.Services.AddScoped<Arbitarr.Core.Identity.IIdentityResolver, Arbitarr.Media.Providers.SeriesTitleResolver>();

// The memo behind SeriesTitleResolver's tvdbid->title lookup. SINGLETON, deliberately: the resolver
// itself is scoped (it reads per-request database state), so a scoped cache would be a fresh empty
// cache on every request and would memoise nothing at all. The entries are a series id and a public
// title with a five-minute TTL -- no per-user or credential-derived state -- so one instance shared
// across requests is correct rather than merely convenient.
builder.Services.AddMemoryCache();

// arb-iiy: the *arr instance epoch the memo key above folds in. SINGLETON for the same reason the
// memo is one: ArrInstanceRepository (which bumps it, scoped, on the admin write request) and
// SeriesTitleResolver (which reads it, scoped, on a later search request) are different instances
// and must see the same counter — a scoped registration would hand each request a zero and the
// repoint would invalidate nothing.
builder.Services.AddSingleton<Arbitarr.Core.Media.IArrInstanceEpoch, Arbitarr.Core.Media.ArrInstanceEpoch>();

// The client the *arr identity lookup rides on. NAMED rather than typed because ArrApiProvider is
// constructed per call around configuration read from the database (see SeriesTitleResolver), so DI
// cannot activate it as a typed client.
//
// AllowAutoRedirect is disabled for the same SSRF reason as the probe above: a misconfigured address
// answering 30x must not make this process reissue a request CARRYING SONARR'S API KEY at a host
// nobody configured.
//
// NO .RemoveAllLoggers() HERE, for the same measured reason as SonarrConnectivityProber above and
// subject to the same condition: ArrApiProvider.BuildEpisodeUri puts the key in the QUERY STRING,
// and .NET's own logging handler collapses the whole query string to "?*" before formatting the
// message. Move the key into a URL PATH segment and neither that collapse nor LogMessageCleanser
// (which does not scrub paths, CLAUDE.md §1) covers it, and this registration would need
// .RemoveAllLoggers().
//
// THE TIMEOUT IS SET HERE, ONCE, and ArrApiProvider must never assign HttpClient.Timeout itself:
// the provider is constructed per call around this POOLED client, and HttpClient throws on that
// assignment once a request has started on the instance, so two concurrent searches were enough to
// make one throw. A caller wanting a shorter bound uses a linked CancellationTokenSource instead --
// SeriesTitleResolver.LookupBudget is exactly that, and is why this longer value is safe here.
builder.Services.AddHttpClient(
        Arbitarr.Media.Providers.SeriesTitleResolver.ArrHttpClientName,
        client => client.Timeout = Arbitarr.Media.Providers.ArrApiProviderOptions.DefaultRequestTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// arb-5uw: ADR 0002's SECOND identity tier -- AniDB's static anime-lists map, consulted by
// SeriesTitleResolver only when the *arr instance above admitted nothing.
//
// REGISTERED UNCONDITIONALLY, AND REQUIRED BY THE RESOLVER. The previous attempt made it an
// optional constructor parameter and registered it nowhere, so DI passed null and the tier was dead
// in production while four tests passed by constructing the provider directly. A required
// dependency turns a missing registration into a startup failure, which is the only place it is
// visible.
//
// REGISTERED IS NOT ACTIVE. Arbitarr:AnimeLists:SourceUrl has NO default here and no upstream is
// named anywhere in this repository, deliberately: which third-party mapping to fetch (and under
// which licence) is the operator's decision, and a committed default would both make that choice
// for them and put a first-run network fetch on the search path nobody opted into. Left unset the
// provider reports NotConfigured with no network call and no filesystem access, so this
// registration costs nothing until an operator sets the value. The inactive case is logged once
// after Build() so the state is discoverable rather than silent.
//
// ConfigDirectory is the SAME configDirectory resolved at the top of this file, not a second read:
// the fetched XML is runtime state and belongs beside the databases under /config (AC21), and a
// second read here could disagree with the first for the per-builder reason that comment sets out.
//
// SINGLETON because the provider's AC19 fetch etiquette is instance state -- the in-process rate
// limiter and the parsed-dataset cache both live on the instance, so a scoped registration would
// hand every request a fresh one that remembers neither and would re-parse the document per search.
// arb-6u6 APPLIES HERE: this is OPERATOR CONFIGURATION, so it must never be parsed with
// `new Uri(...)`. That throws UriFormatException before Build(), and a host that will not boot on
// a typo'd optional setting is a far worse failure than the setting being ignored -- the same rule
// the Ollama startup-fallback client follows at ~:412 for exactly this reason. A malformed value
// therefore leaves the tier INACTIVE, which is already a fully supported state here, rather than
// taking the process down.
//
// The three checks mirror SettingsValidator.ValidateOllamaBaseUrl, which is the repository's
// existing shape for "an operator-supplied address we will issue requests at":
//   - absolute, parseable                 -- a relative or unparseable value has no host to fetch from;
//   - http/https only                     -- SEC-M1: file://, ftp:// and friends are not fetch targets
//                                            we will hand to HttpClient on an operator's behalf;
//   - NO userinfo (user:pw@host)          -- this one is load-bearing for the logging comment below.
//     That comment's premise is that this URI carries NO credential, so the registration safely
//     omits .RemoveAllLoggers(). A userinfo-bearing URL would falsify it: the credential would ride
//     in the logged request URI and land in the persistent store at /api/admin/logs, where neither
//     .NET's query-string redaction nor LogMessageCleanser would scrub it (CLAUDE.md section 1).
//     Rejecting it here is what keeps the comment below true, not a separate nicety.
//
// The rejection is recorded, not logged, because no logger exists before Build(); it is reported
// once beside the inactive notice further down. It names ONLY THE KEY and never the value -- a
// rejected value may be precisely the userinfo-bearing string we refused, and writing it into the
// log store to complain about it would perform the leak the check just prevented.
var animeListsSourceUrlRaw = builder.Configuration["Arbitarr:AnimeLists:SourceUrl"];
var animeListsSourceUrlRejected = false;
Uri? animeListsSourceUrl = null;

if (!string.IsNullOrWhiteSpace(animeListsSourceUrlRaw))
{
    if (Uri.TryCreate(animeListsSourceUrlRaw, UriKind.Absolute, out var parsedAnimeListsSourceUrl)
        && (parsedAnimeListsSourceUrl.Scheme == Uri.UriSchemeHttp
            || parsedAnimeListsSourceUrl.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(parsedAnimeListsSourceUrl.UserInfo))
    {
        animeListsSourceUrl = parsedAnimeListsSourceUrl;
    }
    else
    {
        animeListsSourceUrlRejected = true;
    }
}

var animeListsOptions = new Arbitarr.Media.Providers.AnimeListsProviderOptions(
    SourceUrl: animeListsSourceUrl,
    ConfigDirectory: configDirectory);
builder.Services.AddSingleton(animeListsOptions);
builder.Services.AddSingleton(sp => new Arbitarr.Media.Providers.AnimeListsProvider(
    sp.GetRequiredService<Arbitarr.Media.Providers.AnimeListsProviderOptions>(),
    sp.GetRequiredService<IHttpClientFactory>()
        .CreateClient(Arbitarr.Media.Providers.AnimeListsProvider.HttpClientName)));

// The client the anime-lists fetch rides on. NAMED rather than typed because the provider is a
// singleton built around the options resolved above, so DI cannot activate it as a typed client.
//
// AllowAutoRedirect is disabled for the same SSRF reason as the ArrIdentityLookup registration
// above: an address answering 30x must not make this process reissue the request at a host nobody
// configured. That reason is if anything stronger here, because the address itself is
// operator-supplied configuration rather than a probe target.
//
// NO .RemoveAllLoggers() HERE, and unlike the *arr clients above the reason is simpler rather than
// measured: this request carries NO CREDENTIAL AT ALL -- the anime-lists document is a public
// static file and the URI has no key in its query string or its path -- so there is nothing in the
// logged URI to leak (CLAUDE.md section 1). Logging the address an operator configured is useful.
// If a future upstream ever needs authentication, that stops being true and this registration needs
// .RemoveAllLoggers(), because a token in a URL PATH is scrubbed by neither .NET's query-string
// redaction nor LogMessageCleanser.
//
// THE TIMEOUT IS SET HERE, ONCE, and AnimeListsProvider must never assign HttpClient.Timeout
// itself: it holds this POOLED client for the life of the process, and HttpClient throws on that
// assignment once a request has started on the instance -- the same defect fixed in ArrApiProvider
// under arb-u1c. The provider's constructor carries a comment naming this line as the owner.
builder.Services.AddHttpClient(
        Arbitarr.Media.Providers.AnimeListsProvider.HttpClientName,
        client => client.Timeout = animeListsOptions.EffectiveRequestTimeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// #89: the AI backend's connectivity probe. AllowAutoRedirect is disabled for the same SEC-M5 SSRF
// reason as the OllamaClient registration above -- a misconfigured address answering 30x must not
// make this process issue a request at a host nobody configured.
//
// NO .RemoveAllLoggers() HERE, DELIBERATELY, and the reason is worth stating because the webhook
// client twenty lines below does need it. IHttpClientFactory's logging handler writes the FULL
// absolute request URI at Information, which since #65 lands in the persistent log store served at
// GET /api/admin/logs. That is a durable credential leak when the URI IS the credential (a webhook
// token in the path) -- but Ollama has NO authentication, this probe sends no key, and the settings
// write path rejects a base URL carrying userinfo (SettingsValidator.ValidateOllamaBaseUrl), so
// there is no secret in this URI to leak. Logging the address an operator asked us to test is
// useful rather than dangerous. If that validation is ever relaxed, this comment stops being true
// and the registration needs .RemoveAllLoggers().
builder.Services.AddHttpClient<Arbitarr.Core.Ai.OllamaConnectivityProber>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });


// #55 (foundation shared with #54): the shared event store. Step 1 registered this while nothing
// read or wrote it; steps 2-7 now do both — ScopedEventSink below writes through it, and
// ActivityEndpoint reads through it. Still the ONE store for both issues (plan §2): #54 adds a
// nullable review verdict to the Decision rows here rather than a second table.
builder.Services.AddScoped<Arbitarr.Data.Events.EventRepository>();

// #55 step 2: the emission seam. Singleton because its consumers are singletons (the refresh
// worker) and the request pipeline alike; it creates its own scope per event because
// EventRepository wraps the scoped DbContext, which is not thread-safe. Registered against the
// Arbitarr.Core interface so Arbitarr.Core and Arbitarr.Api can emit without referencing
// Arbitarr.Data -- CoreIsolationTests requires Core to reference no other Arbitarr project.
builder.Services.AddSingleton<Arbitarr.Core.Diagnostics.IEventSink, Arbitarr.Host.Diagnostics.ScopedEventSink>();

// #57: notification configuration and the notifier's durable policy state. Both live as
// colon-namespaced rows in the existing Settings table -- NO NEW TABLE, so there is nothing new for
// MaintenanceJob to prune (the rows are fixed in number, one per setting, and do not accumulate).
// The webhook URL is stored there write-only under a name no SettingKey can produce, exactly as
// source API keys are, so it can never surface through GET /api/admin/settings.
builder.Services.AddScoped<Arbitarr.Data.Notifications.NotificationRepository>();

// #57: the outbound webhook client. AllowAutoRedirect is disabled for the same SSRF reason as the
// NzbHydraSource and SourceConnectivityProber clients above, and it matters more here: the target
// is a URL the operator typed, and a webhook endpoint that answered with a 30x could otherwise
// redirect this process into issuing a request at an address the operator never configured.
// Redirects off means such a response is reported as a rejection instead, which is the truthful
// answer. The URL itself is a secret (providers embed the token in the path), so the transport
// returns a closed enum and never surfaces the target, the response body, or an exception message.
builder.Services.AddHttpClient<Arbitarr.Core.Notifications.WebhookNotificationTransport>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    // AND THE REQUEST URI MUST NOT BE LOGGED. IHttpClientFactory attaches its own logging handler
    // to every named client, which writes "Sending HTTP request POST {Uri}" at Information -- with
    // the FULL absolute URI. For every other client here that is merely noisy; for this one the URI
    // IS the credential (providers embed the token in the path), and since #65 the log store is a
    // persistent SQLite database read back through GET /api/admin/logs, so it would be a DURABLE
    // credential leak rather than a line that scrolls away.
    //
    // The transport's own care -- a closed-enum return, never reading the body, never inspecting
    // exception text -- cannot prevent this, because the leak is in framework code the transport
    // never calls. Suppressing the two logging categories the factory uses is what actually closes
    // it. An integration test drives a real delivery and asserts the URL reaches no log row, so
    // this cannot silently regress if the factory's categories change shape.
    .RemoveAllLoggers();

// #57: the notifier's evaluation loop. POLLS the shared event store through EventRepository's
// seek-cursor read path (#55 step 3) -- there is deliberately no subscribe/observer mechanism over
// that table; see NotificationDispatcher's doc comment. A fresh scope per cycle, like the
// maintenance service below, because the repositories wrap the scoped DbContext.
builder.Services.AddHostedService(sp => new Arbitarr.Host.Notifications.NotificationHostedService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<Arbitarr.Host.Notifications.NotificationHostedService>>()));

// M7-3a: schedules MaintenanceJob on SettingKey.MaintenanceJobInterval. Unlike the RefreshWorker
// options above, the interval is the one setting explicitly permitted to require a restart to take
// effect (see MaintenanceHostedService's doc comment), so it is read once at startup rather than
// via a live options source.
builder.Services.AddHostedService(sp => new Arbitarr.Host.Maintenance.MaintenanceHostedService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<Arbitarr.Host.Maintenance.MaintenanceHostedService>>()));

// arb-fxw: reclaims orphaned files left in BackupPaths.StagingDirectory by a hard kill
// mid-restore/backup (each writer's own `finally` only runs if the process survives to reach it).
// One-shot at startup, not a recurring timer -- see StagingSweepService's doc comment for why.
// Registration order here does not guard against in-flight writers; ExecuteAsync is not awaited
// before the host reports started, so Kestrel may already be serving. The no-race mechanism is
// StagingSweep's processStartUtc cut-off -- see StagingSweepService's doc comment.
builder.Services.AddHostedService(sp => new Arbitarr.Host.Backup.StagingSweepService(
    sp.GetRequiredService<Arbitarr.Data.Backup.BackupPaths>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<Arbitarr.Host.Backup.StagingSweepService>>()));

var app = builder.Build();

// arb-5uw: say, once, that ADR 0002's AnimeLists tier is registered but INACTIVE. Without this the
// state is indistinguishable from the tier being broken -- an operator sees anime titles resolving
// only when Sonarr answers and has nothing anywhere telling them a second tier exists and is
// waiting on one setting. Information rather than Warning: an unset value is the SUPPORTED default,
// not a misconfiguration, precisely because choosing the upstream is the operator's decision.
//
// The message names the KEY and never a URL: there is no upstream to suggest here (see the
// registration above), and the configured value is an operator's address that this line has no
// reason to echo back into the persistent log store.
if (!app.Services.GetRequiredService<Arbitarr.Media.Providers.AnimeListsProvider>().IsConfigured)
{
    var animeListsLogger = app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Arbitarr.Host.Program");

    if (animeListsSourceUrlRejected)
    {
        // WARNING, not Information: an operator who SET this key meant to enable the tier, and the
        // inactive state is now surprising rather than expected. Distinguishing the two is the
        // whole point -- "I never configured it" and "I configured it and it was refused" need
        // different actions, and reporting both at Information would hide the second inside the
        // first for anyone who set a value with a typo.
        //
        // Names ONLY THE KEY. The value is withheld deliberately and permanently: one of the three
        // things that reaches this branch is a userinfo-bearing URL, so echoing the rejected value
        // would write a credential into the persistent log store served at /api/admin/logs -- the
        // exact leak the validation above exists to prevent. AnimeListsSourceUrlValidationTests
        // asserts the absence, with a positive control, for that reason.
        animeListsLogger.LogWarning(
            "The Arbitarr:AnimeLists:SourceUrl configuration key is set but is not a usable " +
            "mapping-document address, so the AnimeLists identity tier is inactive. It must be an " +
            "absolute http or https URL and must not contain credentials (user:password@host). " +
            "The rejected value is deliberately not shown here because it may contain a " +
            "credential. Series titles resolve from the configured *arr instance alone until this " +
            "is corrected.");
    }
    else
    {
        animeListsLogger.LogInformation(
            "The AnimeLists identity tier is inactive because no source URL is configured. Set the " +
            "Arbitarr:AnimeLists:SourceUrl configuration key to a mapping document to enable it; " +
            "until then series titles resolve from the configured *arr instance alone and no " +
            "anime-lists document is fetched.");
    }
}

// SEC-L2: load (or generate, on first run) the per-instance HMAC secret used to compute proxy
// guids, persisted under the configured config directory so it survives restarts. Must run before
// any request is handled, since ReleaseGuid.Compute is called from request handlers.
//
// arb-0hd0: a configured secret wins over the persisted file, and the ONLY caller that supplies one
// is the integration test factory. ReleaseGuid's secret is a mutable process-global, so every host
// build rewrote it; with several hosts starting in one test process, one host's startup could
// change the secret out from under another host's in-flight request. Letting the test factory pin
// one secret per config directory means a second host build for the same config dir is a no-op on
// the static rather than a rewrite, so concurrent hosts stop perturbing each other.
//
// The PRODUCTION path is unchanged: with no such configuration key present -- and nothing in the
// shipped appsettings or the container image sets one -- this is exactly the previous call, a
// secret persisted under /config and generated on first run. This is deliberately not a way to
// supply the secret by environment in production; it exists so tests need not mutate a global.
// Note this narrows the race but does not close it, which is why it is the SECONDARY fix: the
// primary one is RenderedRelease.ProxyGuid being materialised once per instance, so the five
// evaluations per request agree by construction no matter what the static does.
//
// LoadOrCreate IS STILL CALLED UNCONDITIONALLY, and the override applies only to the value handed
// to ReleaseGuid. The secret file is not merely this line's input: BackupService copies
// BackupPaths.SecretKeyPath (release-guid-secret.key) into every archive unconditionally, so a host
// that skipped creating it answered 500 on the backup download and on every status read that
// follows one. Short-circuiting the call broke seven AdminBackupEndpointsTests and three
// BackupSecretExposureTests that way. Creating the file and then overriding the in-memory value
// keeps the on-disk invariant ("a running instance has a secret file") exactly as it was.
//
// arb-pujk: a configured value is VALIDATED before it is used. ReleaseGuid.Configure rejects only
// an empty key, so without this an "AA==" would be accepted as the HMAC-SHA256 key and proxy links
// would become guessable with no error and no log line -- and a value that is not base64 at all
// reached Convert.FromBase64String unguarded, failing startup with a bare FormatException that
// names neither the key nor the requirement. Both are handled here in the same defensive style as
// Arbitarr:Ai:Ollama:KeepAlive above: a named InvalidOperationException that says which key is
// wrong and what to do. The message deliberately carries NO key material -- not the value, not a
// prefix of it, and not its decoded length -- because startup exceptions are logged and this file
// is exactly the kind of value the log cleanser cannot scrub out of a free-form message.
var persistedReleaseGuidSecret = ReleaseGuidSecretFile.LoadOrCreate(configDirectory);
var configuredReleaseGuidSecret = builder.Configuration["Arbitarr:ReleaseGuidSecret"];
byte[] releaseGuidSecret;
if (string.IsNullOrWhiteSpace(configuredReleaseGuidSecret))
{
    releaseGuidSecret = persistedReleaseGuidSecret;
}
else
{
    if (!Convert.TryFromBase64String(
            configuredReleaseGuidSecret,
            new byte[configuredReleaseGuidSecret.Length],
            out _))
    {
        throw new InvalidOperationException(
            "The Arbitarr:ReleaseGuidSecret configuration key is set to a value that is not valid " +
            "base64. It must be a base64-encoded secret of at least 32 bytes. Leave the key unset " +
            "to use the secret persisted under the config directory, which is the supported " +
            "configuration.");
    }

    releaseGuidSecret = Convert.FromBase64String(configuredReleaseGuidSecret);
    if (releaseGuidSecret.Length < 32)
    {
        throw new InvalidOperationException(
            "The Arbitarr:ReleaseGuidSecret configuration key is set to a value that decodes to " +
            "fewer than 32 bytes. It must be a base64-encoded secret of at least 32 bytes, because " +
            "it is used as an HMAC-SHA256 key and a shorter one makes proxy links guessable. Leave " +
            "the key unset to use the secret persisted under the config directory, which is the " +
            "supported configuration.");
    }

    // arb-q75n: recommend-only from the #220 security review. A configured override is silent
    // otherwise, so a value copy-pasted across installs (sharing one instance's proxy-guid HMAC
    // key with another) would never surface anywhere an operator would see it. Warn here, naming
    // only the key -- never the value, its length, or a hash of it, for the same reason arb-pujk's
    // validation exceptions above carry no key material: this file is exactly what the log
    // cleanser cannot scrub out of a free-form message. Skipped in Development, where honouring a
    // configured override is expected (e.g. the integration test factory, see arb-0hd0 above).
    // No environment gate beyond this log line -- that decision is owner-pending and separate from
    // this warning, and no entropy/all-zero check -- both are explicitly out of scope for arb-q75n.
    if (!builder.Environment.IsDevelopment())
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Arbitarr.Host.Program")
            .LogWarning(
                "The Arbitarr:ReleaseGuidSecret configuration key is set, so the release-guid " +
                "secret is being taken from configuration instead of the file persisted under the " +
                "config directory. A value copy-pasted across installs is now shared between them.");
    }
}

ReleaseGuid.Configure(releaseGuidSecret);

// #56: seed the last-backup timestamp from the automatic archives already on disk, so a restart
// does not report "never backed up" beside a directory full of them. Deliberately after the secret
// load above and before any request is served, for the same reason: the Backup tab must never show
// an operator a stale-safety-net answer that is merely an artefact of the process having restarted.
app.Services.GetRequiredService<Arbitarr.Data.Backup.BackupStateStore>()
    .ReconcileFromDisk(app.Services.GetRequiredService<Arbitarr.Data.Backup.BackupPaths>());

// M7-11: apply pending migrations on startup so a container starting from a clean /config
// volume self-provisions its schema before any endpoint (dashboard included) tries to query
// it. This runs on a dedicated scope (not the app's root scope) so the DbContext is disposed
// immediately after. A failure here is always fatal to startup — there is no safe way to serve
// requests against a database that isn't at the expected schema version — so we catch only to
// wrap the raw EF/SQLite exception in a clear, actionable message before re-throwing, which
// stops the host loop before app.Run() rather than crashing later on the first request.
// arb-itmm: convert the database file to WAL exactly once, HERE, before anything opens it
// concurrently. On a fresh file "PRAGMA journal_mode = WAL" is not bounded by busy_timeout (8087 ms
// measured against a 5000 ms timeout, and unbounded while a blocker holds on), so leaving the
// conversion on the per-connection open path meant a container's first start could hang with no
// timeout anywhere to break it once several hosted services opened the new database at once. This
// statement runs single-threaded, before the migration scope below opens the first connection and
// long before app.Run() starts the hosted services, so there is no concurrent opener to wait on.
// SqliteConnectionFactory.OpenConnection now only VERIFIES the mode; do not move the conversion
// back there. This also carries the auto_vacuum=INCREMENTAL pragma, which only takes effect on the
// connection that CREATES the file — which, after this change, is this one.
try
{
    app.Services.GetRequiredService<SqliteConnectionFactory>().ConvertToWalOnce();
}
catch (Exception ex)
{
    throw new InvalidOperationException(
        $"Arbitarr failed to convert '{databasePath}' to WAL journal mode on startup. " +
        "The container cannot serve requests against a database that is not in the expected " +
        "journal mode. Check that the /config volume is writable and not corrupted, then " +
        "restart. See the inner exception for the underlying EF Core/SQLite error.", ex);
}

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
    try
    {
        dbContext.Database.Migrate();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Arbitarr failed to apply database migrations for '{databasePath}' on startup. " +
            "The container cannot serve requests against a database that is not at the expected " +
            "schema version. Check that the /config volume is writable and not corrupted, then " +
            "restart. See the inner exception for the underlying EF Core/SQLite error.", ex);
    }

    // #53 stage 53b: seed the sources table from the environment on a first run with an empty table,
    // then resolve the source configuration in force from the database and publish it for the
    // IUpstreamSource factory and NzbHydraConfigurationStatus registered above. This must run after
    // Migrate() (the table may not exist before it) and before app.Run() (nothing may serve a request
    // against an unresolved configuration). Reuses this same scope for both reasons.
    await SourceSeeder.SeedAndResolveAsync(
        dbContext,
        resolvedSourceConfiguration,
        nzbHydraEnvironment,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Arbitarr.Host.Sources"));

    // #89: the same seed-once-then-DB ruling, applied to the Ollama base URL. Must run after
    // Migrate() (the Settings table may not exist before it) and before app.Run(). Unlike the
    // source resolution above, nothing is published to a singleton here: the value is read per use
    // through OllamaBaseUrlResolver, which is what makes a later change take effect without a
    // restart. This call only ensures the row exists and warns about a divergent environment value.
    await Arbitarr.Host.Ai.OllamaBaseUrlSeeder.SeedAsync(
        dbContext,
        builder.Configuration.GetSection("Arbitarr:Ai:Ollama")["BaseUrl"],
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Arbitarr.Host.Ai"));

    // #112: the same ruling again, for the model. Separate call rather than folded into the seeder
    // above because the two rows are independent — an existing deployment already has a base URL row
    // from #89 and must still get a model row seeded on this upgrade, which a combined
    // "seed if neither exists" check would have skipped, leaving the model invisible forever on
    // exactly the installations #112 is for.
    await Arbitarr.Host.Ai.OllamaModelSeeder.SeedAsync(
        dbContext,
        builder.Configuration.GetSection("Arbitarr:Ai:Ollama")["Model"],
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Arbitarr.Host.Ai"));
}

app.UseDefaultFiles();
app.UseStaticFiles();

// Issue #46: the informational version now comes from the same BuildInfo singleton
// GET /api/system/build reports, instead of a separately-read AssemblyInformationalVersionAttribute
// lookup that could drift from it. /health stays a minimal liveness probe -- only `version`
// changes here; no other build fields are added to it (that is what /api/system/build is for).
app.MapGet("/health", (Arbitarr.Api.SystemInfo.BuildInfo buildInfo) => Results.Json(new
{
    status = "ok",
    name = "Arbitarr",
    version = buildInfo.InformationalVersion,
}))
    .WithClassification(RouteClassification.PublicRead);

StatusEndpoint.Map(app);
RecentSearchesEndpoint.Map(app);
EffectiveConfigEndpoint.Map(app);
HealthStalenessEndpoint.Map(app);
// #55 step 3: the paged, filterable read over the shared event store. PublicRead per plan §3.2,
// now settled rather than provisional -- #59 closed with D2 unamended (the admin key gates
// mutating actions; reading is not one). See ActivityEndpoint's own note.
ActivityEndpoint.Map(app);
// #54 steps 3-5: the review queue over the SAME event store's Decision rows -- the two reads and
// the review write. Mapped in one call because the three are one feature, but they are NOT one
// classification: the reads are PublicRead on ActivityEndpoint's settled precedent (same store,
// same #59/D2 ruling), while the review write is AdminMutating under /api/admin/. The split is by
// what each route DOES, never by its HTTP verb. See DecisionReviewEndpoints' own note.
DecisionReviewEndpoints.Map(app);
Arbitarr.Api.SystemInfo.BuildInfoEndpoint.Map(app);
AdminPingEndpoint.Map(app);
ObservabilityEndpoint.Map(app);
LogsEndpoint.Map(app);
AdminSettingsEndpoints.Map(app);
AdminSecurityEndpoints.Map(app);
AdminBackupEndpoints.Map(app);
AdminApiKeyEndpoints.Map(app);
// #44: /api/auth/*. Classified PublicRead — meaning "not wrapped by AdminApiKeyFilter", which is
// exactly right for a surface whose job is to authenticate a caller who has no credential yet.
// Each route carries its own guard instead; see AuthEndpoints' type doc.
AuthEndpoints.Map(app);
AdminSourceEndpoints.Map(app);
AdminNotificationEndpoints.Map(app);
// #89: the AI backend's own admin surface (read/write/probe the Ollama base URL). Separate from
// AdminSettingsEndpoints because the value is off SettingsCatalog.Entries -- see AdminAiEndpoints.
AdminAiEndpoints.Map(app);
// arb-u1c: the Sonarr instance's own admin surface (read/write/clear/probe the base URL and the
// write-only API key). Separate from AdminSettingsEndpoints for BOTH of the reasons the codebase
// already has: the key is a secret whose colon-namespaced row no SettingKey can produce, and the
// base URL needs a connectivity probe the generic catalog row cannot offer. See AdminArrEndpoints.
AdminArrEndpoints.Map(app);
// arb-6l9b.1: the Radarr instance's own admin surface, mirroring the Sonarr one above for the same
// two reasons and deliberately NOT sharing an implementation with it (arb-arrq D3 -- the rejected
// generalisation is recorded in RadarrInstanceRepository's type doc). See AdminRadarrEndpoints.
AdminRadarrEndpoints.Map(app);
// arb-6l9b.3: the queue reads for both kinds. ONE surface for both, unlike the two configuration
// surfaces above, because the queue wire contract is identical for Sonarr and Radarr (the upstream
// v3 contract is) while their configuration contracts are free to diverge -- the reasoning is on
// AdminArrQueueEndpoints' type doc. Both routes are CONCRETE rather than templated (paging is
// query-string) so AdminApiKeyRouteEnumerationTests' sweep covers them; it skips every
// {-containing route by design.
AdminArrQueueEndpoints.Map(app);
// arb-6l9b.4: the library reads for both kinds -- GET .../sonarr/series and .../radarr/movies. One
// surface for both for the same reason the queue reads are, and CONCRETE rather than templated for
// the same reason: filtering and paging are query-string, so AdminApiKeyRouteEnumerationTests' sweep
// (which skips every {-containing route by design) covers them.
AdminArrLibraryEndpoints.Map(app);
AdminRuleEndpoints.Map(app);
AdHocSearchEndpoint.Map(app);
MatchExplanationEndpoint.Map(app);
SuppressionViewEndpoint.Map(app);

// Torznab family (torrent-oriented: namespace prefix "torznab", enclosure MIME application/x-bittorrent).
app.MapGet("/torznab/api", async (
    string? t,
    string? q,
    string? cat,
    int? limit,
    int? offset,
    // #104: bound as string, not int?, so an EMPTY value (tvdbid=) is treated as absent rather
    // than rejected by minimal-API binding with a 400 and a text/plain BadHttpRequestException
    // body — a non-XML answer from a route that must always answer in Torznab/Newznab XML. See
    // IdParamClamp.ParseOptional.
    string? tvdbid,
    string? tmdbid,
    string? season,
    string? ep,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
    RecentSearchLog recentSearchLog,
    Arbitarr.Core.Diagnostics.IEventSink eventSink,
    // arb-u1c: nullable so the route still resolves when no Sonarr instance is configured; the
    // registration below is conditional on nothing, but the resolver itself returns null throughout
    // when the instance rows are absent.
    Arbitarr.Core.Identity.IIdentityResolver? identityResolver,
    // arb-tps: the durable tier the search writes to, so the links this response carries still
    // resolve after a restart and past the in-memory tier's 30-minute TTL.
    IReleaseLookupStore releaseLookupStore,
    // arb-zwk: so a failed store write is recorded rather than silent. The endpoint degrades on that
    // failure and still answers, which without a log would be an invisible loss of durability.
    ILoggerFactory loggerFactory,
    ISourceRegistry sourceRegistry,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var (clientContext, apiKeyError) = await ApiKeyValidator.ValidateAsync(apikey, apiKeyResolver, isTorznab: true, cancellationToken).ConfigureAwait(false);
    if (apiKeyError is not null)
    {
        return apiKeyError;
    }

    if (string.Equals(t, "caps", StringComparison.OrdinalIgnoreCase))
    {
        return await CapsEndpoint.HandleTorznabAsync(capsAggregator, sourceRegistry, cancellationToken).ConfigureAwait(false);
    }

    var categories = ParseCategories(cat);
    return await SearchEndpoint.HandleTorznabAsync(
        t,
        q,
        categories,
        PagingClamp.ClampLimit(limit),
        PagingClamp.ClampOffset(offset),
        apikey!,
        snapshotService,
        filterStage,
        releaseLookup,
        recentSearchLog,
        eventSink,
        request,
        cancellationToken,
        IdParamClamp.ClampProviderId(IdParamClamp.ParseOptional(tvdbid)),
        IdParamClamp.ClampProviderId(IdParamClamp.ParseOptional(tmdbid)),
        IdParamClamp.ClampSeason(IdParamClamp.ParseOptional(season)),
        IdParamClamp.ClampEpisode(IdParamClamp.ParseOptional(ep)),
        clientContext?.Name,
        identityResolver,
        releaseLookupStore,
        loggerFactory.CreateLogger(typeof(SearchEndpoint).FullName!)).ConfigureAwait(false);
})
    .WithClassification(RouteClassification.PublicRead);

// Newznab family (Usenet-oriented: namespace prefix "newznab", enclosure MIME application/x-nzb).
app.MapGet("/newznab/api", async (
    string? t,
    string? q,
    string? cat,
    int? limit,
    int? offset,
    // #104: bound as string, not int?, so an EMPTY value (tvdbid=) is treated as absent rather
    // than rejected by minimal-API binding with a 400 and a text/plain BadHttpRequestException
    // body — a non-XML answer from a route that must always answer in Torznab/Newznab XML. See
    // IdParamClamp.ParseOptional.
    string? tvdbid,
    string? tmdbid,
    string? season,
    string? ep,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
    RecentSearchLog recentSearchLog,
    Arbitarr.Core.Diagnostics.IEventSink eventSink,
    // arb-u1c: nullable so the route still resolves when no Sonarr instance is configured; the
    // registration below is conditional on nothing, but the resolver itself returns null throughout
    // when the instance rows are absent.
    Arbitarr.Core.Identity.IIdentityResolver? identityResolver,
    // arb-tps: see the torznab route's note on this parameter.
    IReleaseLookupStore releaseLookupStore,
    // arb-zwk: see the torznab route's note on this parameter.
    ILoggerFactory loggerFactory,
    ISourceRegistry sourceRegistry,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var (clientContext, apiKeyError) = await ApiKeyValidator.ValidateAsync(apikey, apiKeyResolver, isTorznab: false, cancellationToken).ConfigureAwait(false);
    if (apiKeyError is not null)
    {
        return apiKeyError;
    }

    if (string.Equals(t, "caps", StringComparison.OrdinalIgnoreCase))
    {
        return await CapsEndpoint.HandleNewznabAsync(capsAggregator, sourceRegistry, cancellationToken).ConfigureAwait(false);
    }

    var categories = ParseCategories(cat);
    return await SearchEndpoint.HandleNewznabAsync(
        t,
        q,
        categories,
        PagingClamp.ClampLimit(limit),
        PagingClamp.ClampOffset(offset),
        apikey!,
        snapshotService,
        filterStage,
        releaseLookup,
        recentSearchLog,
        eventSink,
        request,
        cancellationToken,
        IdParamClamp.ClampProviderId(IdParamClamp.ParseOptional(tvdbid)),
        IdParamClamp.ClampProviderId(IdParamClamp.ParseOptional(tmdbid)),
        IdParamClamp.ClampSeason(IdParamClamp.ParseOptional(season)),
        IdParamClamp.ClampEpisode(IdParamClamp.ParseOptional(ep)),
        clientContext?.Name,
        identityResolver,
        releaseLookupStore,
        loggerFactory.CreateLogger(typeof(SearchEndpoint).FullName!)).ConfigureAwait(false);
})
    .WithClassification(RouteClassification.PublicRead);

app.MapGet("/download/{proxyGuid}", async (
    string proxyGuid,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    IReleaseLookup releaseLookup,
    ISourceRegistry sourceRegistry,
    Arbitarr.Core.Diagnostics.IEventSink eventSink,
    Arbitarr.Core.Diagnostics.IDownloadRefusalTracker refusalTracker,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
    await DownloadProxyEndpoint.HandleAsync(proxyGuid, apikey, apiKeyResolver, releaseLookup, sourceRegistry, eventSink, cancellationToken, refusalTracker, timeProvider).ConfigureAwait(false))
    .WithClassification(RouteClassification.PublicRead);

// Terminal 404 for unmatched /api/ paths, so a typo'd, renamed or removed API route fails
// loudly instead of being swallowed by the SPA fallback below and answered 200 + index.html.
//
// THE .RequireHost-STYLE METHOD SCOPE ON THE NEXT LINE IS LOAD-BEARING. An unscoped
// `app.MapFallback("/api/{*rest}", ...)` -- which is what the plan specified -- matches EVERY
// HTTP method, and that silently breaks real, registered, mutating admin routes:
//
//   POST /api/admin/rules       -> 404   (should be 401/503 from AdminApiKeyFilter)
//   POST /api/admin/rules/test  -> 404
//   PUT  /api/admin/settings/{key} -> 404
//
// Measured, not theorised: with the unscoped form,
// AdminApiKeyRouteEnumerationTests.Every_AdminMutating_route_rejects_requests_without_the_admin_key
// fails on the first such route, and a probe over the whole admin surface showed every non-GET
// verb falling through to this terminal while every GET still reached its real endpoint.
//
// WHY. When a request's PATH matches a real endpoint but its METHOD does not, routing does not
// stop at that endpoint -- the method-mismatch candidate loses and the next matching candidate
// is consulted. An all-methods catch-all at Order = int.MaxValue is always still standing, so it
// wins and answers 404 in place of the gate's 401/503. This is not the "unmatched path" case the
// terminal exists for; it is a real route being shadowed. Restricting the terminal to GET/HEAD
// removes it from every mutating verb's candidate set entirely, so those routes resolve to their
// own endpoints (405 where the verb genuinely is not registered) and the gate runs as designed.
//
// GET/HEAD is also exactly the right scope on the merits: this terminal exists solely to stop
// MapFallbackToFile from swallowing API paths, and MapFallbackToFile is itself GET/HEAD-only.
// A mutating verb could never have reached the SPA fallback, so it never needed this guard.
//
// ORDERING IS NOT THE MECHANISM, and an executor who thinks it is will "helpfully" reorder these
// two lines and later conclude the guard is flaky. Both MapFallback and MapFallbackToFile set
// Order = int.MaxValue, so they tie on order and both sort behind every real endpoint; the tie
// breaks on ROUTE-TEMPLATE PRECEDENCE, where a literal segment outranks a catch-all. Hence
// "/api/{*rest}" (literal `api` first) beats the SPA fallback's "{*path:nonfile}" regardless of
// which line is written first.
//
// The SPA fallback's {*path:nonfile} constraint is a SECOND, INDEPENDENT filter: it already
// excludes any path whose last segment contains a dot. So an extensioned probe
// (/api/admin/foo.json) 404s whether or not this terminal exists and cannot verify it -- an
// extensionless probe (/api/admin/foo) is the one this line is actually responsible for.
//
// PublicRead is the honest classification -- it mutates nothing and requires no key. It is also
// required: RouteClassificationTests enumerates the live EndpointDataSource and fails on any
// endpoint missing the metadata, and fallbacks are real RouteEndpoints in that source.
// AdminApiKeyRouteEnumerationTests skips templates containing '{', so it does not probe these.
app.MapFallback("/api/{*rest}", () => Results.NotFound())
    .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }))
    .WithClassification(RouteClassification.PublicRead);

// SPA deep links: /settings, /rules, ... return index.html so react-router resolves them
// client-side after a hard reload (AC4).
app.MapFallbackToFile("index.html")
    .WithClassification(RouteClassification.PublicRead);

app.Run();

static IReadOnlyList<int> ParseCategories(string? cat) =>
    string.IsNullOrWhiteSpace(cat)
        ? Array.Empty<int>()
        : cat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var id) ? id : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            // Security-m3 LOW #5: an unbounded cat= list is embedded verbatim in the cache key
            // (via SearchCacheKeyBuilder's category component) -- cap it before it reaches the
            // key, same rationale as PagingClamp/IdParamClamp above.
            .Distinct()
            .Take(64)
            .ToArray();

// Exposes the top-level-statement entry point as a named type so integration tests can host
// this app in-process via WebApplicationFactory<Program> (M2-1/M2-2/M2-3 etc., plan §M2).
public partial class Program;
