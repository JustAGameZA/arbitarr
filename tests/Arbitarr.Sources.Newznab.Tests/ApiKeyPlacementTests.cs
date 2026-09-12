using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Arbitarr.Core.Sources;

namespace Arbitarr.Sources.Newznab.Tests;

/// <summary>
/// The api-key must reach upstream in the QUERY STRING and nowhere else — see
/// <c>NewznabSource.AppendApiKey</c> for why (the framework's URI redaction collapses the query
/// string but prints every path segment in full, and LogMessageCleanser scrubs query strings only,
/// so a key in the path lands in the persistent log store verbatim).
///
/// <para><b>Every assertion here goes through <see cref="AssertKeyOnlyInQueryString"/>, and the
/// positive controls below run that same helper against deliberately mis-built requests.</b> This
/// is the non-vacuity discipline CLAUDE.md §4 requires: an <c>Assert.DoesNotContain(key, path)</c>
/// passes just as happily when the key was never in play at all, so before asserting that the real
/// request is clean, the helper is first shown to FAIL on a request with the key in the path and on
/// one with the key in a header. Only then does the clean result mean anything.</para>
/// </summary>
public class ApiKeyPlacementTests
{
    private const string ApiKey = "placeholder-planted-key";

    private static NewznabSourceOptions MakeOptions(string apiPath = "/api") => new(
        BaseUrl: new Uri("http://indexer.example:9117/"),
        ApiPath: apiPath,
        ApiKey: ApiKey,
        SourceName: "test-indexer",
        RequestTimeout: TimeSpan.FromSeconds(2),
        RateLimitMaxCalls: 1000,
        RateLimitInterval: TimeSpan.FromMilliseconds(1));

    /// <summary>
    /// The single assertion helper both the positive controls and the real assertions use. It
    /// throws when the key appears anywhere other than the query string. Returning void and
    /// throwing (rather than returning a bool) is deliberate: a caller cannot accidentally ignore
    /// the result.
    /// </summary>
    private static void AssertKeyOnlyInQueryString(Uri uri, IReadOnlyList<string> headerLines)
    {
        Assert.DoesNotContain(ApiKey, uri.AbsolutePath, StringComparison.Ordinal);

        var offendingHeaders = headerLines
            .Where(line => line.Contains(ApiKey, StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(offendingHeaders);

        // And the key must actually BE in the query string — without this the helper would pass on
        // a request that carried no key at all, which is the vacuous shape the whole file guards
        // against.
        Assert.Contains(ApiKey, uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void PositiveControl_TheHelper_Fails_WhenTheKeyIsInThePath()
    {
        var misbuilt = new Uri($"http://indexer.example:9117/api/{ApiKey}/search?t=search&apikey={ApiKey}");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertKeyOnlyInQueryString(misbuilt, Array.Empty<string>()));
    }

    [Fact]
    public void PositiveControl_TheHelper_Fails_WhenTheKeyIsInAHeader()
    {
        using var misbuilt = new HttpRequestMessage(HttpMethod.Get, $"http://indexer.example:9117/api?t=search&apikey={ApiKey}");
        misbuilt.Headers.Add("X-Api-Key", ApiKey);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertKeyOnlyInQueryString(misbuilt.RequestUri!, FakeHttpMessageHandler.FlattenHeaders(misbuilt)));
    }

    [Fact]
    public void PositiveControl_TheHelper_Fails_WhenTheRequestCarriesNoKeyAtAll()
    {
        var keyless = new Uri("http://indexer.example:9117/api?t=search");

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => AssertKeyOnlyInQueryString(keyless, Array.Empty<string>()));
    }

    [Fact]
    public async Task SearchAsync_SendsTheApiKey_OnlyInTheQueryString()
    {
        var handler = OkHandler(EmptyFeed);
        var source = new NewznabSource(MakeOptions(), new HttpClient(handler), new FakeCircuitBreaker());

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Protocol: SearchProtocol.Newznab, Limit: 10));

        AssertKeyOnlyInQueryString(Assert.Single(handler.RequestedUris), Assert.Single(handler.RequestedHeaderLines));
    }

    [Fact]
    public async Task GetCapsAsync_SendsTheApiKey_OnlyInTheQueryString()
    {
        var handler = OkHandler(EmptyCaps);
        var source = new NewznabSource(MakeOptions(), new HttpClient(handler), new FakeCircuitBreaker());

        await source.GetCapsAsync(SearchProtocol.Newznab);

        AssertKeyOnlyInQueryString(Assert.Single(handler.RequestedUris), Assert.Single(handler.RequestedHeaderLines));
    }

    /// <summary>
    /// A configured ApiPath is not an opening for the key to move into the path: the path segment
    /// is operator-supplied and the key is appended as a query parameter regardless of its shape.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithANonDefaultApiPath_StillSendsTheApiKey_OnlyInTheQueryString()
    {
        var handler = OkHandler(EmptyFeed);
        var source = new NewznabSource(
            MakeOptions("/api/v2.0/indexers/example/results/torznab"),
            new HttpClient(handler),
            new FakeCircuitBreaker());

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Protocol: SearchProtocol.Torznab, Limit: 10));

        AssertKeyOnlyInQueryString(Assert.Single(handler.RequestedUris), Assert.Single(handler.RequestedHeaderLines));
    }

    private const string EmptyFeed =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel /></rss>";

    private const string EmptyCaps =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><caps><categories /></caps>";

    private static FakeHttpMessageHandler OkHandler(string body) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/xml").MediaType!),
    });
}
