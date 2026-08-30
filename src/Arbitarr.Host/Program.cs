using System.Reflection;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Dashboard;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Routing;
using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Caching;
using Arbitarr.Data.CircuitBreaker;
using Arbitarr.Data.Security;
using Arbitarr.Data.Settings;
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
builder.Services.AddScoped<PaginationSnapshotService>();

builder.Services.AddHostedService(sp => new RefreshWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    new RefreshWorkerOptions(
        RefreshWorkerDefaults.WorkerEnabled,
        RefreshWorkerDefaults.WorkerCycleInterval,
        RefreshWorkerDefaults.ActiveWindow,
        RefreshWorkerDefaults.RefreshLead,
        RefreshWorkerDefaults.FreshUntilAge,
        RefreshWorkerDefaults.ServeUntilAge,
        RefreshWorkerDefaults.RepopulationSpreadWindow,
        RefreshWorkerDefaults.MaxConcurrentRefreshes),
    builder.Configuration["Arbitarr:Sources:NzbHydra:SourceName"] ?? "NZBHydra2",
    logger: sp.GetRequiredService<ILogger<RefreshWorker>>()));
builder.Services.AddSingleton<InMemoryReleaseLookup>();
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

var app = builder.Build();

// SEC-L2: load (or generate, on first run) the per-instance HMAC secret used to compute proxy
// guids, persisted under the configured config directory so it survives restarts. Must run before
// any request is handled, since ReleaseGuid.Compute is called from request handlers.
ReleaseGuid.Configure(ReleaseGuidSecretFile.LoadOrCreate(configDirectory));

// Apply pending migrations on startup so a fresh /config volume gets a usable schema
// before any endpoint (dashboard included) tries to query it.
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
    dbContext.Database.Migrate();
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
AdminSettingsEndpoints.Map(app);

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
    InMemoryReleaseLookup releaseLookup,
    RecentSearchLog recentSearchLog,
    IReadOnlyList<IUpstreamSource> sources,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var (_, apiKeyError) = ApiKeyValidator.Validate(apikey, apiKeyResolver, isTorznab: true);
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
        releaseLookup,
        recentSearchLog,
        request,
        cancellationToken,
        IdParamClamp.ClampProviderId(tvdbid),
        IdParamClamp.ClampProviderId(tmdbid),
        IdParamClamp.ClampSeason(season),
        IdParamClamp.ClampEpisode(ep)).ConfigureAwait(false);
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
    InMemoryReleaseLookup releaseLookup,
    RecentSearchLog recentSearchLog,
    IReadOnlyList<IUpstreamSource> sources,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    var (_, apiKeyError) = ApiKeyValidator.Validate(apikey, apiKeyResolver, isTorznab: false);
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
        releaseLookup,
        recentSearchLog,
        request,
        cancellationToken,
        IdParamClamp.ClampProviderId(tvdbid),
        IdParamClamp.ClampProviderId(tmdbid),
        IdParamClamp.ClampSeason(season),
        IdParamClamp.ClampEpisode(ep)).ConfigureAwait(false);
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
