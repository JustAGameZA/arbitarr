using Arbitarr.Ai;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Dashboard;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Routing;
using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Caching;
using Arbitarr.Data.CircuitBreaker;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Maintenance;
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
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// #53 stage 53b: NzbHydraSourceOptions requires a non-null absolute Uri, but a deployment can now
// legitimately have *no* source configured (empty sources table, no environment configuration) —
// a state the pre-53b env-var wiring could not represent, since it always defaulted to a URL. The
// adapter is still constructed in that case so the search pipeline shape is unchanged; it is simply
// pointed at RFC 5737 TEST-NET-1, which is guaranteed non-routable, so any request fails fast and
// UpstreamMergeStage degrades to an empty result set exactly as it does for an unreachable source.
// The alternative — registering no IUpstreamSource at all — would change the DI shape and is 53d's
// call to make once the UI can add sources.
const string UnconfiguredSourceBaseUrl = "http://192.0.2.1:1";

// Runtime state lives under /config (AC21), overridable via ARBITARR_CONFIG_DIR for local
// dev/test so a real /config directory is never required outside the production container.
var configDirectory = Environment.GetEnvironmentVariable("ARBITARR_CONFIG_DIR") ?? "/config";
Arbitarr.Host.Provisioning.DatasetProvisioner.EnsureProvisioned(configDirectory);
var databasePath = Path.Combine(configDirectory, "arbitarr.db");

builder.Services.AddSingleton(new SqliteConnectionOptions { DatabasePath = databasePath });
builder.Services.AddSingleton<SqliteConnectionFactory>();
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
var resolvedSourceConfiguration = new ResolvedSourceConfiguration();
builder.Services.AddSingleton(resolvedSourceConfiguration);

// "Configured" means an API key is present — the same predicate the pre-53b env-var wiring used, so
// the /api/config/effective contract is unchanged (plan §3.4 defers that to 53d). Only the *source*
// of the answer moved, from the environment to the resolved database row. Registered as a factory
// rather than an instance because the resolution has not happened yet at this point in startup; the
// singleton is first resolved on a request, long after SourceSeeder has run. The dashboard's
// effective-config view (M2 §2, D1 surface 3) reports this without ever exposing the key itself.
builder.Services.AddSingleton(sp => new NzbHydraConfigurationStatus(
    IsConfigured: sp.GetRequiredService<ResolvedSourceConfiguration>().IsConfigured));

// SEC-M1 (SSRF): the source adapter validates <link> origins itself, but disabling automatic
// redirect-following here is defense in depth — an upstream response could otherwise 30x us to an
// arbitrary host and we'd fetch it before the origin check ever saw the real target.
builder.Services.AddHttpClient<NzbHydraSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IUpstreamSource>(sp =>
{
    // Read per scope from the startup-resolved configuration rather than captured from the
    // environment at registration time.
    var resolved = sp.GetRequiredService<ResolvedSourceConfiguration>();
    var options = new NzbHydraSourceOptions(
        new Uri(resolved.BaseUrl ?? UnconfiguredSourceBaseUrl),
        resolved.ApiKey ?? string.Empty,
        resolved.SourceName ?? "NZBHydra2");
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient(nameof(NzbHydraSource));
    var circuitBreaker = sp.GetRequiredService<IAsyncCircuitBreaker>();
    return new NzbHydraSource(options, httpClient, circuitBreaker);
});
builder.Services.AddScoped<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
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
builder.Services.AddScoped(sp => new PaginationSnapshotService(
    sp.GetRequiredService<UpstreamMergeStage>(),
    sp.GetRequiredService<SearchResultCacheStage>(),
    sp.GetRequiredService<IQuerySnapshotStore>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ISnapshotTtlSource>()));

// M7-7/R20: worker-health snapshot, singleton so both the hosted RefreshWorker (writer) and
// StatusEndpoint (reader) share the same instance across the app's lifetime.
builder.Services.AddSingleton<RefreshWorkerHealthTracker>(_ => new RefreshWorkerHealthTracker(RefreshWorkerDefaults.WorkerEnabled));
builder.Services.AddSingleton<IRefreshWorkerHealth>(sp => sp.GetRequiredService<RefreshWorkerHealthTracker>());

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
    health: sp.GetRequiredService<IRefreshWorkerHealth>()));

// AI layer (M5, Step 6): Arbitarr.Ai has zero references to Arbitarr.Data/Arbitarr.Media (AC6a,
// enforced by Arbitarr.Architecture.Tests.AiMediaIsolationTests/DependencyDirectionTests) — Host
// is the sole place that composes it with its persistence-backed cache reader/writer and the
// shared circuit breaker (keyed by source name "Ollama", same IAsyncCircuitBreaker instance every
// other adapter uses). Base URL defaults to the in-cluster service name, never a LAN IP; tests use
// http://ollama.example.invalid.
builder.Services.AddSingleton(_ =>
{
    var section = builder.Configuration.GetSection("Arbitarr:Ai:Ollama");
    var baseUrlRaw = section["BaseUrl"] ?? "http://ollama:11434";
    var model = section["Model"] ?? "qwen2.5:7b-instruct-q4_K_M";
    var keepAlive = section["KeepAlive"] ?? "-1";
    return new OllamaOptions(new Uri(baseUrlRaw), model, keepAlive);
});
builder.Services.AddSingleton(sp =>
{
    var section = builder.Configuration.GetSection("Arbitarr:Ai");
    var modelName = section["ModelName"] ?? sp.GetRequiredService<OllamaOptions>().Model;
    var modelDigest = section["ModelDigest"] ?? "unknown";
    var promptVersion = section["PromptVersion"] ?? "v1";
    return new AiModelIdentity(modelName, modelDigest, promptVersion);
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
    return new OllamaClient(options, httpClient, circuitBreaker);
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
    sp.GetRequiredService<ObservabilityCounters>()));

builder.Services.AddSingleton<InMemoryReleaseLookup>();

// ClassifierPollingWorker is the BackgroundService that drives ClassifierWorker (a plain Scoped
// type): it snapshots InMemoryReleaseLookup each cycle and runs classification + (AC26b/R17)
// worker-side title-rewrite caching. AC24: the poll interval is
// re-read from settings at the top of every cycle via the scope factory, so a live settings change
// takes effect without a restart.
builder.Services.AddHostedService(sp => new ClassifierPollingWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<InMemoryReleaseLookup>(),
    sp.GetRequiredService<AiModelIdentity>(),
    sp.GetRequiredService<TimeProvider>(),
    logger: sp.GetRequiredService<ILogger<ClassifierPollingWorker>>()));
builder.Services.AddSingleton<IReleaseLookup>(sp => sp.GetRequiredService<InMemoryReleaseLookup>());

// Inbound Torznab/Newznab client apikey (M1-9, security-hardened). Distinct from
// Arbitarr:Sources:NzbHydra:ApiKey (the upstream NZBHydra2 credential Arbitarr uses to call out)
// and from SettingKey.AdminApiKey (a separate M4/M7 concept). "Arbitarr:ClientApiKeys:<n>:Name"/
// "...:Key" configures named keys; a single legacy "Arbitarr:ApiKey" value collapses to one named
// key, "default", for backward compatibility.
builder.Services.AddSingleton<IClientApiKeyResolver>(_ =>
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

// D2 admin API key gate (M7-6): reads SettingKey.AdminApiKey from the settings store, distinct
// from the Torznab/Newznab client apikey resolved above. Scoped: captures the scoped ArbitarrDbContext.
builder.Services.AddScoped<IAdminApiKeyReader, DbAdminApiKeyReader>();
builder.Services.AddScoped<AdminApiKeyFilter>();

// M7-5 settings write path: shares the same measured *arr RSS sync interval as EffectiveSettingsReader
// above, so read and write validation agree on cross-field bounds (e.g. FreshUntilCeiling).
builder.Services.AddScoped(sp => new SettingsRepository(
    sp.GetRequiredService<ArbitarrDbContext>(),
    TimeSpan.FromMinutes(15)));

// #53 stage 53a: persistence only. Nothing reads from SourceRepository yet — env vars remain
// authoritative until 53b adds the DB-first, env-var-fallback resolution path (plan §3.2). Registered
// now so 53b-53d can depend on it without another Host change.
builder.Services.AddScoped<SourceRepository>();

// #55 step 1 (foundation shared with #54): the shared event store. Deliberately unread/unwritten
// until the emission stage (#55 step 2 / #54 step 2) — registered now so those stages can depend on
// it without another Host change, matching #53 stage 53a's posture for SourceRepository above.
builder.Services.AddScoped<Arbitarr.Data.Events.EventRepository>();

// M7-3a: schedules MaintenanceJob on SettingKey.MaintenanceJobInterval. Unlike the RefreshWorker
// options above, the interval is the one setting explicitly permitted to require a restart to take
// effect (see MaintenanceHostedService's doc comment), so it is read once at startup rather than
// via a live options source.
builder.Services.AddHostedService(sp => new Arbitarr.Host.Maintenance.MaintenanceHostedService(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<Arbitarr.Host.Maintenance.MaintenanceHostedService>>()));

var app = builder.Build();

// SEC-L2: load (or generate, on first run) the per-instance HMAC secret used to compute proxy
// guids, persisted under the configured config directory so it survives restarts. Must run before
// any request is handled, since ReleaseGuid.Compute is called from request handlers.
ReleaseGuid.Configure(ReleaseGuidSecretFile.LoadOrCreate(configDirectory));

// M7-11: apply pending migrations on startup so a container starting from a clean /config
// volume self-provisions its schema before any endpoint (dashboard included) tries to query
// it. This runs on a dedicated scope (not the app's root scope) so the DbContext is disposed
// immediately after. A failure here is always fatal to startup — there is no safe way to serve
// requests against a database that isn't at the expected schema version — so we catch only to
// wrap the raw EF/SQLite exception in a clear, actionable message before re-throwing, which
// stops the host loop before app.Run() rather than crashing later on the first request.
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
Arbitarr.Api.SystemInfo.BuildInfoEndpoint.Map(app);
AdminPingEndpoint.Map(app);
ObservabilityEndpoint.Map(app);
AdminSettingsEndpoints.Map(app);
AdminSecurityEndpoints.Map(app);
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
    int? tvdbid,
    int? tmdbid,
    int? season,
    int? ep,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
    RecentSearchLog recentSearchLog,
    IReadOnlyList<IUpstreamSource> sources,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var (clientContext, apiKeyError) = ApiKeyValidator.Validate(apikey, apiKeyResolver, isTorznab: true);
    if (apiKeyError is not null)
    {
        return apiKeyError;
    }

    if (string.Equals(t, "caps", StringComparison.OrdinalIgnoreCase))
    {
        return await CapsEndpoint.HandleTorznabAsync(capsAggregator, sources, cancellationToken).ConfigureAwait(false);
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
        request,
        cancellationToken,
        IdParamClamp.ClampProviderId(tvdbid),
        IdParamClamp.ClampProviderId(tmdbid),
        IdParamClamp.ClampSeason(season),
        IdParamClamp.ClampEpisode(ep),
        clientContext?.Name).ConfigureAwait(false);
})
    .WithClassification(RouteClassification.PublicRead);

// Newznab family (Usenet-oriented: namespace prefix "newznab", enclosure MIME application/x-nzb).
app.MapGet("/newznab/api", async (
    string? t,
    string? q,
    string? cat,
    int? limit,
    int? offset,
    int? tvdbid,
    int? tmdbid,
    int? season,
    int? ep,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
    RecentSearchLog recentSearchLog,
    IReadOnlyList<IUpstreamSource> sources,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var (clientContext, apiKeyError) = ApiKeyValidator.Validate(apikey, apiKeyResolver, isTorznab: false);
    if (apiKeyError is not null)
    {
        return apiKeyError;
    }

    if (string.Equals(t, "caps", StringComparison.OrdinalIgnoreCase))
    {
        return await CapsEndpoint.HandleNewznabAsync(capsAggregator, sources, cancellationToken).ConfigureAwait(false);
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
        request,
        cancellationToken,
        IdParamClamp.ClampProviderId(tvdbid),
        IdParamClamp.ClampProviderId(tmdbid),
        IdParamClamp.ClampSeason(season),
        IdParamClamp.ClampEpisode(ep),
        clientContext?.Name).ConfigureAwait(false);
})
    .WithClassification(RouteClassification.PublicRead);

app.MapGet("/download/{proxyGuid}", async (
    string proxyGuid,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    IReleaseLookup releaseLookup,
    IReadOnlyList<IUpstreamSource> sources,
    CancellationToken cancellationToken) =>
    await DownloadProxyEndpoint.HandleAsync(proxyGuid, apikey, apiKeyResolver, releaseLookup, sources, cancellationToken).ConfigureAwait(false))
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
