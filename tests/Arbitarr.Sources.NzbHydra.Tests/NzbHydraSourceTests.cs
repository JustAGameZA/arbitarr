using System.Net;
using System.Text;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Sources.NzbHydra.Tests;

public class NzbHydraSourceTests
{
    private static NzbHydraSourceOptions MakeOptions(int maxUpstreamCallsPerSearch = 3) => new(
        BaseUrl: new Uri("http://hydra.example.test:5076/"),
        ApiKey: "secret-api-key",
        SourceName: "test-hydra",
        RequestTimeout: TimeSpan.FromSeconds(2),
        MaxUpstreamPageSize: 100,
        MaxUpstreamCallsPerSearch: maxUpstreamCallsPerSearch,
        RateLimitMaxCalls: 1000,
        RateLimitInterval: TimeSpan.FromMilliseconds(1));

    private static string BuildTorznabXml(int itemCount, int startIndex = 0)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<rss xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>");
        for (var i = 0; i < itemCount; i++)
        {
            var idx = startIndex + i;
            sb.Append("<item>");
            sb.Append($"<title>Release {idx}</title>");
            sb.Append($"<guid>guid-{idx}</guid>");
            sb.Append("<link>http://hydra.example.test:5076/download/" + idx + "</link>");
            sb.Append("<pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>");
            sb.Append("<torznab:attr name=\"size\" value=\"12345\" />");
            sb.Append("<torznab:attr name=\"category\" value=\"5000\" />");
            sb.Append("<torznab:attr name=\"protocol\" value=\"torrent\" />");
            sb.Append("</item>");
        }

        sb.Append("</channel></rss>");
        return sb.ToString();
    }

    private static HttpClient MakeHttpClient(FakeHttpMessageHandler handler) => new(handler);

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }

    [Fact]
    public async Task SearchAsync_SinglePage_HappyPath_ReturnsParsedResults()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildTorznabXml(5), Encoding.UTF8, "application/xml"),
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var results = await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 5));

        Assert.Equal(5, results.Count);
        Assert.Equal("Release 0", results[0].Title);
        Assert.Equal("guid-0", results[0].Guid);
        Assert.Equal(12345, results[0].Size);
        Assert.Single(handler.RequestedUris);
        Assert.Equal(1, breaker.SuccessCount);
    }

    [Fact]
    public async Task SearchAsync_FanOut_WhenLimitExceeds100_IssuesMultiplePagedRequestsAndConcatenates()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(request =>
        {
            callCount++;
            // First page: full 100. Second page: remaining 50 (limit 150 total).
            var query = ParseQuery(request.RequestUri!);
            var offset = int.Parse(query["offset"]);
            var limit = int.Parse(query["limit"]);
            var itemCount = offset == 0 ? 100 : Math.Min(limit, 50);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildTorznabXml(itemCount, offset), Encoding.UTF8, "application/xml"),
            };
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var results = await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 150));

        Assert.Equal(2, callCount);
        Assert.Equal(150, results.Count);
        Assert.Equal("Release 0", results[0].Title);
        Assert.Equal("Release 149", results[149].Title);

        // Verify offsets used across the paged requests.
        var offsets = handler.RequestedUris
            .Select(u => int.Parse(ParseQuery(u)["offset"]))
            .ToArray();
        Assert.Equal(new[] { 0, 100 }, offsets);
    }

    [Fact]
    public async Task SearchAsync_FanOut_StopsAtMaxUpstreamCallsPerSearch_EvenIfMoreResultsRequested()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Always return a full page so fan-out would continue indefinitely without the cap.
            Content = new StringContent(BuildTorznabXml(100), Encoding.UTF8, "application/xml"),
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(maxUpstreamCallsPerSearch: 2), MakeHttpClient(handler), breaker);

        var results = await source.SearchAsync(new SearchQuery(null, Array.Empty<int>(), Limit: 1000));

        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.Equal(200, results.Count);
    }

    [Fact]
    public async Task SearchAsync_PassesApiKey_AsQueryStringParameter_NeverAsHeader()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildTorznabXml(1), Encoding.UTF8, "application/xml"),
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 1));

        var requestedUri = Assert.Single(handler.RequestedUris);
        var query = ParseQuery(requestedUri);
        Assert.Equal("secret-api-key", query["apikey"]);
    }

    [Fact]
    public async Task SearchAsync_WhenCircuitBreakerRefusesCall_ShortCircuitsWithoutHttpCall()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("HTTP call should never happen when breaker is open."));
        var breaker = new FakeCircuitBreaker();
        breaker.SetCanCall(false);
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var results = await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 10));

        Assert.Empty(results);
        Assert.Empty(handler.RequestedUris);
        Assert.True(breaker.CanCallCallCount >= 1);
    }

    [Fact]
    public async Task SearchAsync_OnHttpFailure_RecordsFailureOnCircuitBreakerAndPropagates()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 10)));

        Assert.Single(breaker.Failures);
        Assert.Equal(0, breaker.SuccessCount);
    }

    [Fact]
    public async Task GetCapsAsync_ParsesCategoriesAndSearchSupportFromCapsXml()
    {
        const string capsXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <caps>
              <searching>
                <search available="yes" supportedParams="q" />
                <tv-search available="yes" supportedParams="q,season,ep" />
                <movie-search available="no" supportedParams="q" />
              </searching>
              <categories>
                <category id="5000" name="TV" />
                <category id="2000" name="Movies" />
              </categories>
              <limits max="100" default="100" />
            </caps>
            """;
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(capsXml, Encoding.UTF8, "application/xml"),
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var caps = await source.GetCapsAsync();

        Assert.Contains(5000, caps.SupportedCategories);
        Assert.Contains(2000, caps.SupportedCategories);
        Assert.True(caps.SupportsTvSearch);
        Assert.False(caps.SupportsMovieSearch);
        Assert.Equal(100, caps.MaxPageSize);
    }

    [Fact]
    public async Task GetCapsAsync_WhenCircuitBreakerRefusesCall_ShortCircuitsWithoutHttpCall()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("HTTP call should never happen when breaker is open."));
        var breaker = new FakeCircuitBreaker();
        breaker.SetCanCall(false);
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var caps = await source.GetCapsAsync();

        Assert.Empty(caps.SupportedCategories);
        Assert.Empty(handler.RequestedUris);
    }

    // SEC-M1 (SSRF): an item whose <link> does not match the configured NZBHydra origin
    // (scheme+host+port) must be dropped entirely rather than parsed with a placeholder/blank URI.
    [Fact]
    public async Task SearchAsync_DropsItems_WhenLinkOriginDoesNotMatchConfiguredBaseUrl()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
            <item>
              <title>Legit release</title>
              <guid>guid-legit</guid>
              <link>http://hydra.example.test:5076/download/legit</link>
              <pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>
              <torznab:attr name="size" value="12345" />
              <torznab:attr name="category" value="5000" />
              <torznab:attr name="protocol" value="torrent" />
            </item>
            <item>
              <title>SSRF attempt</title>
              <guid>guid-evil</guid>
              <link>http://192.0.2.99:9999/internal/secrets</link>
              <pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>
              <torznab:attr name="size" value="12345" />
              <torznab:attr name="category" value="5000" />
              <torznab:attr name="protocol" value="torrent" />
            </item>
            </channel></rss>
            """;
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var results = await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 5));

        var single = Assert.Single(results);
        Assert.Equal("Legit release", single.Title);
    }

    // SEC-M2: an upstream 429/503 must surface as RequestLimitReachedException, not a bare
    // HttpRequestException from EnsureSuccessStatusCode (which the proxy would otherwise turn into
    // an unhandled 5xx instead of the designed rate-limit signal).
    [Fact]
    public async Task SearchAsync_On429_ThrowsRequestLimitReachedException()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        await Assert.ThrowsAsync<RequestLimitReachedException>(() =>
            source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 5)));

        Assert.Single(breaker.Failures);
    }

    [Fact]
    public async Task SearchAsync_On503_ThrowsRequestLimitReachedException()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        await Assert.ThrowsAsync<RequestLimitReachedException>(() =>
            source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 5)));
    }

    // SEC-M1 (SSRF): a <link> whose scheme doesn't match (e.g. an upstream trying to smuggle a
    // non-http(s) scheme) must be dropped at parse time just like a host/port mismatch.
    [Fact]
    public async Task SearchAsync_DropsItem_WhenLinkSchemeDoesNotMatchConfiguredBaseUrl()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
            <item>
              <title>Legit release</title>
              <guid>guid-legit</guid>
              <link>http://hydra.example.test:5076/download/legit</link>
              <pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>
              <torznab:attr name="size" value="12345" />
              <torznab:attr name="category" value="5000" />
              <torznab:attr name="protocol" value="torrent" />
            </item>
            <item>
              <title>Scheme mismatch attempt</title>
              <guid>guid-scheme</guid>
              <link>ftp://hydra.example.test:5076/download/evil</link>
              <pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>
              <torznab:attr name="size" value="12345" />
              <torznab:attr name="category" value="5000" />
              <torznab:attr name="protocol" value="torrent" />
            </item>
            </channel></rss>
            """;
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var results = await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Limit: 5));

        var single = Assert.Single(results);
        Assert.Equal("Legit release", single.Title);
    }

    // SEC-M1 (SSRF), fetch-time re-validation: parse-time filtering only guarantees the link was
    // same-origin when the feed was parsed, not that it still is at fetch time. FetchDownloadAsync
    // must re-check scheme+host+port immediately before the HTTP call and refuse (mapped to a 502
    // by the download proxy's HttpRequestException catch) rather than trust a stale/mutated link.
    [Fact]
    public async Task FetchDownloadAsync_WhenLinkOriginDoesNotMatchConfiguredBaseUrl_ThrowsHttpRequestException()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("HTTP call should never happen for an origin-mismatched link."));
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var release = new ReleaseCandidate
        {
            Title = "Mismatched link",
            Guid = "guid-mismatch",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("http://192.0.2.99:9999/internal/secrets"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchDownloadAsync(release));

        Assert.Empty(handler.RequestedUris);
    }

    // SEC-M1: an upstream 302 redirect must not be silently followed to a possibly-different host.
    // The fake handler never auto-follows redirects (it just returns whatever response it's given),
    // mirroring AllowAutoRedirect=false: EnsureSuccessStatusCode treats 302 as a failure, mapped to
    // 502 by the download proxy, and no second request is made to the Location host.
    [Fact]
    public async Task FetchDownloadAsync_On302Redirect_ThrowsAndMakesNoSecondRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://192.0.2.99:9999/internal/secrets");
            return response;
        });
        var breaker = new FakeCircuitBreaker();
        var source = new NzbHydraSource(MakeOptions(), MakeHttpClient(handler), breaker);

        var release = new ReleaseCandidate
        {
            Title = "Redirecting release",
            Guid = "guid-redirect",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("http://hydra.example.test:5076/download/redirect"),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchDownloadAsync(release));

        var requestedUri = Assert.Single(handler.RequestedUris);
        Assert.Equal("hydra.example.test", requestedUri.Host);
    }
}
