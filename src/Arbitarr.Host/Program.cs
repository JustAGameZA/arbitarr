using System.Reflection;
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
using Arbitarr.Host;
using Arbitarr.Host.Caching;
using Arbitarr.Host.Security;
using Arbitarr.Sources.NzbHydra;
using Microsoft.EntityFrameworkCore;

// Arbitarr.Host is the explicit composition root: the only project permitted to
// reference source-adapter and other outer-layer projects (AC6). Currently minimal —
// other steps extend DI wiring and config binding here.
var builder = WebApplication.CreateBuilder(args);

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

var nzbHydraSection = builder.Configuration.GetSection("Arbitarr:Sources:NzbHydra");
var nzbHydraBaseUrlRaw = nzbHydraSection["BaseUrl"] ?? "http://127.0.0.1:5076";
var nzbHydraApiKey = nzbHydraSection["ApiKey"] ?? string.Empty;
var nzbHydraSourceName = nzbHydraSection["SourceName"] ?? "NZBHydra2";

// "Configured" means an API key is present (M1 wiring); the dashboard's effective-config view
// (M2 §2, D1 surface 3) reports this without ever exposing the key itself.
builder.Services.AddSingleton(new NzbHydraConfigurationStatus(IsConfigured: !string.IsNullOrWhiteSpace(nzbHydraApiKey)));

// SEC-M1 (SSRF): the source adapter validates <link> origins itself, but disabling automatic
// redirect-following here is defense in depth — an upstream response could otherwise 30x us to an
// arbitrary host and we'd fetch it before the origin check ever saw the real target.
builder.Services.AddHttpClient<NzbHydraSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IUpstreamSource>(sp =>
{
    var options = new NzbHydraSourceOptions(new Uri(nzbHydraBaseUrlRaw), nzbHydraApiKey, nzbHydraSourceName);
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

builder.Services.AddHostedService(sp => new RefreshWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    builder.Configuration["Arbitarr:Sources:NzbHydra:SourceName"] ?? "NZBHydra2",
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
}

app.UseDefaultFiles();
app.UseStaticFiles();

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "dev";

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    name = "Arbitarr",
    version,
}))
    .WithClassification(RouteClassification.PublicRead);

StatusEndpoint.Map(app);
RecentSearchesEndpoint.Map(app);
EffectiveConfigEndpoint.Map(app);
HealthStalenessEndpoint.Map(app);
AdminPingEndpoint.Map(app);
ObservabilityEndpoint.Map(app);
AdminSettingsEndpoints.Map(app);
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
