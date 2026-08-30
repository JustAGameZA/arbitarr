using System.Reflection;
using Arbitarr.Ai;
using Arbitarr.Api.Search;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.CircuitBreaker;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
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

builder.Services.AddHttpClient<NzbHydraSource>();
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
builder.Services.AddScoped<SettingsReader>();

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
builder.Services.AddHttpClient(nameof(OllamaClient));
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
    sourceName: "Ollama"));

builder.Services.AddScoped<FilterStage>(sp => new FilterStage(
    sp.GetRequiredService<FilterProfileLoader>(),
    sp.GetRequiredService<SettingsReader>(),
    sp.GetRequiredService<ArbitarrDbContext>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IVerdictCacheReader>(),
    sp.GetRequiredService<AiModelIdentity>()));
builder.Services.AddSingleton<InMemoryReleaseLookup>();
builder.Services.AddSingleton<IReleaseLookup>(sp => sp.GetRequiredService<InMemoryReleaseLookup>());

var app = builder.Build();

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "dev";

// Inbound Torznab/Newznab client apikey (M1-9) — authenticates *arr clients calling into
// Arbitarr. Distinct from Arbitarr:Sources:NzbHydra:ApiKey, which is the upstream NZBHydra2
// credential Arbitarr uses to call out.
var inboundApiKey = builder.Configuration["Arbitarr:ApiKey"] ?? string.Empty;

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
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
    IReadOnlyList<IUpstreamSource> sources,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    if (ApiKeyValidator.Validate(request.Query["apikey"], inboundApiKey, isTorznab: true) is { } apiKeyError)
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
        limit ?? 100,
        offset ?? 0,
        snapshotService,
        filterStage,
        releaseLookup,
        request,
        cancellationToken).ConfigureAwait(false);
});

// Newznab family (Usenet-oriented: namespace prefix "newznab", enclosure MIME application/x-nzb).
app.MapGet("/newznab/api", async (
    string? t,
    string? q,
    string? cat,
    int? limit,
    int? offset,
    CapsAggregator capsAggregator,
    PaginationSnapshotService snapshotService,
    FilterStage filterStage,
    InMemoryReleaseLookup releaseLookup,
    IReadOnlyList<IUpstreamSource> sources,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    if (ApiKeyValidator.Validate(request.Query["apikey"], inboundApiKey, isTorznab: false) is { } apiKeyError)
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
        limit ?? 100,
        offset ?? 0,
        snapshotService,
        filterStage,
        releaseLookup,
        request,
        cancellationToken).ConfigureAwait(false);
});

app.MapGet("/download/{proxyGuid}", async (
    string proxyGuid,
    IReleaseLookup releaseLookup,
    IReadOnlyList<IUpstreamSource> sources,
    CancellationToken cancellationToken) =>
    await DownloadProxyEndpoint.HandleAsync(proxyGuid, releaseLookup, sources, cancellationToken).ConfigureAwait(false));

app.Run();

static IReadOnlyList<int> ParseCategories(string? cat) =>
    string.IsNullOrWhiteSpace(cat)
        ? Array.Empty<int>()
        : cat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var id) ? id : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
