using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>Minimal single-key resolver double for exercising <see cref="ApiKeyValidator"/> in isolation.</summary>
internal sealed class SingleKeyResolver : IClientApiKeyResolver
{
    private readonly string? _expectedKey;

    public SingleKeyResolver(string? expectedKey) => _expectedKey = expectedKey;

    public ClientKeyContext? Resolve(string? apikey) =>
        !string.IsNullOrEmpty(_expectedKey) && string.Equals(apikey, _expectedKey, StringComparison.Ordinal)
            ? new ClientKeyContext("default")
            : null;
}

/// <summary>
/// Golden-XML tests for error rendering (M1-9): both protocol families render the same
/// &lt;error code="..." description="..."/&gt; element, and RequestLimitReachedException's
/// rate-limit code path never surfaces as a 5xx (that behavior is exercised end-to-end in
/// Arbitarr.Api's SearchEndpoint; here we confirm the rendered XML shape it produces).
/// </summary>
public sealed class ErrorXmlGoldenTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-errorxmlgolden-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public void Torznab_error_renders_code_and_description()
    {
        var xml = TorznabXmlWriter.WriteError(500, "Request limit reached");
        var rendered = xml.ToString();

        Assert.Contains("<error code=\"500\" description=\"Request limit reached\" />", rendered);
    }

    [Fact]
    public void Newznab_error_renders_identical_shape_to_torznab()
    {
        var torznab = TorznabXmlWriter.WriteError(500, "Request limit reached").ToString();
        var newznab = NewznabXmlWriter.WriteError(500, "Request limit reached").ToString();

        Assert.Equal(torznab, newznab);
    }

    [Fact]
    public void Rate_limit_error_uses_SearchEndpoint_documented_code()
    {
        var xml = TorznabXmlWriter.WriteError(Arbitarr.Api.Search.SearchEndpoint.RateLimitErrorCode, "Request limit reached");
        Assert.Contains($"code=\"{Arbitarr.Api.Search.SearchEndpoint.RateLimitErrorCode}\"", xml.ToString());
    }

    [Fact]
    public void Missing_apikey_renders_code_100_in_torznab_wrapper()
    {
        var (context, error) = ApiKeyValidator.Validate(providedApiKey: null, new SingleKeyResolver("correct-key"), isTorznab: true);

        Assert.Null(context);
        Assert.NotNull(error);
        var body = RenderedBody(error!);
        Assert.Contains("<error code=\"100\" description=\"Incorrect user credentials\" />", body);
        Assert.DoesNotContain("newznab", body);
    }

    [Fact]
    public void Wrong_apikey_renders_code_100_in_newznab_wrapper()
    {
        var (context, error) = ApiKeyValidator.Validate(providedApiKey: "wrong-key", new SingleKeyResolver("correct-key"), isTorznab: false);

        Assert.Null(context);
        Assert.NotNull(error);
        var body = RenderedBody(error!);
        Assert.Contains("<error code=\"100\" description=\"Incorrect user credentials\" />", body);
    }

    [Fact]
    public void Correct_apikey_returns_no_error_result()
    {
        var (context, error) = ApiKeyValidator.Validate(providedApiKey: "correct-key", new SingleKeyResolver("correct-key"), isTorznab: true);

        Assert.NotNull(context);
        Assert.Null(error);
    }

    [Fact]
    public void Empty_configured_apikey_never_authenticates_any_request()
    {
        var (context, error) = ApiKeyValidator.Validate(providedApiKey: "anything", new SingleKeyResolver(null), isTorznab: true);

        Assert.Null(context);
        Assert.NotNull(error);
    }

    /// <summary>
    /// M1-9: the apikey-error XML is a well-formed Torznab/Newznab response body, not a transport-level
    /// failure — it must render with HTTP 200 (Results.Text with no explicit status code), the same as
    /// every other search response, so *arr clients parse the &lt;error&gt; element instead of treating
    /// this as a network failure.
    /// </summary>
    [Fact]
    public void Wrong_apikey_error_result_renders_with_http_status_200()
    {
        var (context, error) = ApiKeyValidator.Validate(providedApiKey: "wrong-key", new SingleKeyResolver("correct-key"), isTorznab: true);

        Assert.Null(context);
        Assert.NotNull(error);
        var statusCode = RenderedStatusCode(error!);
        Assert.Equal(200, statusCode);
    }

    /// <summary>
    /// M1-9: SearchEndpoint's RequestLimitReached path renders a Torznab &lt;error&gt; body (protocol-level
    /// error code 500 embedded in the XML), but that must never surface as an HTTP 5xx — the endpoint
    /// stays reachable/non-erroring at the transport level so *arr clients can read and log the error XML.
    /// </summary>
    [Fact]
    public async Task Rate_limited_search_endpoint_result_does_not_surface_as_http_5xx()
    {
        var source = new FakeUpstreamSource("eztv", searchException: new RequestLimitReachedException("eztv"));
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var snapshotService = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time);
        var releaseLookup = new InMemoryReleaseLookup();

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");

        using var context = CreateContext();
        var filterStage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            time);

        var result = await SearchEndpoint.HandleTorznabAsync(
            "search",
            null,
            Array.Empty<int>(),
            50,
            0,
            "correct-key",
            snapshotService,
            filterStage,
            releaseLookup,
            new RecentSearchLog(),
            httpContext.Request,
            CancellationToken.None);

        using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteAsync(httpContext);

        Assert.True(httpContext.Response.StatusCode < 500);
    }

    private static string RenderedBody(Microsoft.AspNetCore.Http.IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        using var body = new MemoryStream();
        context.Response.Body = body;

        result.ExecuteAsync(context).GetAwaiter().GetResult();

        body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(body);
        return reader.ReadToEnd();
    }

    private static int RenderedStatusCode(Microsoft.AspNetCore.Http.IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        using var body = new MemoryStream();
        context.Response.Body = body;

        result.ExecuteAsync(context).GetAwaiter().GetResult();

        return context.Response.StatusCode;
    }
}
