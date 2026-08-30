using System.Reflection;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.CircuitBreaker;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Arbitarr.Host.Security;
using Arbitarr.Sources.NzbHydra;
using Microsoft.EntityFrameworkCore;

// Arbitarr.Host is the explicit composition root: the only project permitted to
// reference source-adapter and other outer-layer projects (AC6). Currently minimal —
// other steps extend DI wiring and config binding here.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(_ =>
{
    var configured = builder.Configuration["Arbitarr:Database:Path"];
    return new SqliteConnectionOptions
    {
        DatabasePath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "arbitarr.db")
            : configured,
    };
});
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

// SEC-M1 (SSRF): the source adapter validates <link> origins itself, but disabling automatic
// redirect-following here is defense in depth — an upstream response could otherwise 30x us to an
// arbitrary host and we'd fetch it before the origin check ever saw the real target.
builder.Services.AddHttpClient<NzbHydraSource>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IUpstreamSource>(sp =>
{
    var section = builder.Configuration.GetSection("Arbitarr:Sources:NzbHydra");
    var baseUrlRaw = section["BaseUrl"] ?? "http://127.0.0.1:5076";
    var apiKey = section["ApiKey"] ?? string.Empty;
    var sourceName = section["SourceName"] ?? "NZBHydra2";

    var options = new NzbHydraSourceOptions(new Uri(baseUrlRaw), apiKey, sourceName);
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient(nameof(NzbHydraSource));
    var circuitBreaker = sp.GetRequiredService<IAsyncCircuitBreaker>();
    return new NzbHydraSource(options, httpClient, circuitBreaker);
});
builder.Services.AddScoped<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
builder.Services.AddScoped<UpstreamMergeStage>();
builder.Services.AddScoped<IQuerySnapshotStore, QuerySnapshotStore>();
builder.Services.AddScoped<PaginationSnapshotService>();
builder.Services.AddScoped<FilterProfileLoader>();
builder.Services.AddScoped<ApiKeyProfileResolver>();
builder.Services.AddScoped<SettingsReader>();
builder.Services.AddScoped<FilterStage>();
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

var app = builder.Build();

// SEC-L2: load (or generate, on first run) the per-instance HMAC secret used to compute proxy
// guids, persisted under the configured config directory so it survives restarts. Must run before
// any request is handled, since ReleaseGuid.Compute is called from request handlers.
var configDirectory = builder.Configuration["Arbitarr:ConfigDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "config");
ReleaseGuid.Configure(ReleaseGuidSecretFile.LoadOrCreate(configDirectory));

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "dev";

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    name = "Arbitarr",
    version,
}));

// Torznab family (torrent-oriented: namespace prefix "torznab", enclosure MIME application/x-bittorrent).
app.MapGet("/torznab/api", async (
    string? t,
    string? q,
    string? cat,
    int? limit,
    int? offset,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
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
        request,
        cancellationToken,
        clientContext?.Name).ConfigureAwait(false);
});

// Newznab family (Usenet-oriented: namespace prefix "newznab", enclosure MIME application/x-nzb).
app.MapGet("/newznab/api", async (
    string? t,
    string? q,
    string? cat,
    int? limit,
    int? offset,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
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
        request,
        cancellationToken,
        clientContext?.Name).ConfigureAwait(false);
});

app.MapGet("/download/{proxyGuid}", async (
    string proxyGuid,
    string? apikey,
    IClientApiKeyResolver apiKeyResolver,
    IReleaseLookup releaseLookup,
    IReadOnlyList<IUpstreamSource> sources,
    CancellationToken cancellationToken) =>
    await DownloadProxyEndpoint.HandleAsync(proxyGuid, apikey, apiKeyResolver, releaseLookup, sources, cancellationToken).ConfigureAwait(false));

app.Run();

static IReadOnlyList<int> ParseCategories(string? cat) =>
    string.IsNullOrWhiteSpace(cat)
        ? Array.Empty<int>()
        : cat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var id) ? id : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
