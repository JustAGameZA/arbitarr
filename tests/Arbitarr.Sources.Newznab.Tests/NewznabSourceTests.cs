using System.Net;
using System.Text;
using Arbitarr.Core.Sources;

namespace Arbitarr.Sources.Newznab.Tests;

/// <summary>
/// Adapter-level tests for <see cref="NewznabSource"/>: endpoint construction from the Source row's
/// ApiPath, the rate-limit and redirect refusals, and the per-source timeout.
/// </summary>
public class NewznabSourceTests
{
    private static NewznabSourceOptions MakeOptions(
        string apiPath = "/api",
        string baseUrl = "http://indexer.example:9117/",
        TimeSpan? requestTimeout = null) => new(
        BaseUrl: new Uri(baseUrl),
        ApiPath: apiPath,
        ApiKey: "test-api-key",
        SourceName: "test-indexer",
        RequestTimeout: requestTimeout ?? TimeSpan.FromSeconds(2),
        RateLimitMaxCalls: 1000,
        RateLimitInterval: TimeSpan.FromMilliseconds(1));

    private const string EmptyFeed =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel /></rss>";

    private const string OneItemFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
          <item>
            <title>Example Release</title>
            <guid>guid-1</guid>
            <link>http://indexer.example:9117/download/1</link>
            <torznab:attr name="size" value="4096" />
          </item>
        </channel></rss>
        """;

    private static FakeHttpMessageHandler Answering(HttpStatusCode status, string body = EmptyFeed) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        });

    private static SearchQuery AnyQuery() =>
        new("bleach", Array.Empty<int>(), Protocol: SearchProtocol.Newznab, Limit: 10);

    // ---------------------------------------------------------------------
    // Endpoint: the path comes from the row, and NZBHydra2's protocol-selected
    // /torznab/api rule (#99) is deliberately NOT applied.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_UsesTheConfiguredApiPath()
    {
        var handler = Answering(HttpStatusCode.OK);
        var source = new NewznabSource(
            MakeOptions("/api/v2.0/indexers/example/results/torznab"),
            new HttpClient(handler),
            new FakeCircuitBreaker());

        await source.SearchAsync(AnyQuery());

        Assert.Equal("/api/v2.0/indexers/example/results/torznab", Assert.Single(handler.RequestedUris).AbsolutePath);
    }

    /// <summary>
    /// A Torznab-protocol search must NOT be rerouted to <c>/torznab/api</c>. That rewrite is
    /// NZBHydra2's aggregator behaviour (#99); a direct indexer serves one feed at the path its
    /// operator configured, and rewriting it sends the search to a path the indexer does not serve.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithTheTorznabProtocol_DoesNotRewriteThePathToTorznabApi()
    {
        var handler = Answering(HttpStatusCode.OK);
        var source = new NewznabSource(MakeOptions("/api"), new HttpClient(handler), new FakeCircuitBreaker());

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), Protocol: SearchProtocol.Torznab, Limit: 10));

        Assert.Equal("/api", Assert.Single(handler.RequestedUris).AbsolutePath);
    }

    [Fact]
    public async Task GetCapsAsync_WithTheTorznabProtocol_DoesNotRewriteThePathToTorznabApi()
    {
        var handler = Answering(HttpStatusCode.OK, "<caps><categories /></caps>");
        var source = new NewznabSource(MakeOptions("/api"), new HttpClient(handler), new FakeCircuitBreaker());

        await source.GetCapsAsync(SearchProtocol.Torznab);

        Assert.Equal("/api", Assert.Single(handler.RequestedUris).AbsolutePath);
    }

    /// <summary>
    /// A base URL with its own path prefix (a reverse proxy hosting the indexer at a sub-path) has
    /// the ApiPath appended to it, not substituted for it — an operator who configured
    /// <c>/jackett/</c> is not silently rerouted to the host root.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AppendsTheApiPathToABaseUrlThatHasItsOwnPathPrefix()
    {
        var handler = Answering(HttpStatusCode.OK);
        var source = new NewznabSource(
            MakeOptions("/api", baseUrl: "http://indexer.example:9117/jackett/"),
            new HttpClient(handler),
            new FakeCircuitBreaker());

        await source.SearchAsync(AnyQuery());

        Assert.Equal("/jackett/api", Assert.Single(handler.RequestedUris).AbsolutePath);
    }

    [Fact]
    public async Task GetCapsAsync_RequestsTheCapsMode()
    {
        var handler = Answering(HttpStatusCode.OK, "<caps><categories /></caps>");
        var source = new NewznabSource(MakeOptions(), new HttpClient(handler), new FakeCircuitBreaker());

        await source.GetCapsAsync(SearchProtocol.Newznab);

        Assert.Contains("t=caps", Assert.Single(handler.RequestedUris).Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_HappyPath_ParsesTheFeed()
    {
        var handler = Answering(HttpStatusCode.OK, OneItemFeed);
        var source = new NewznabSource(MakeOptions(), new HttpClient(handler), new FakeCircuitBreaker());

        var results = await source.SearchAsync(AnyQuery());

        Assert.Equal("guid-1", Assert.Single(results).Guid);
    }

    // ---------------------------------------------------------------------
    // Refusals (SEC-M2 rate limiting, ADR 0014 redirects)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_On429_ThrowsRequestLimitReached()
    {
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.TooManyRequests)), new FakeCircuitBreaker());

        await Assert.ThrowsAsync<RequestLimitReachedException>(() => source.SearchAsync(AnyQuery()));
    }

    [Fact]
    public async Task SearchAsync_On503_ThrowsRequestLimitReached()
    {
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.ServiceUnavailable)), new FakeCircuitBreaker());

        await Assert.ThrowsAsync<RequestLimitReachedException>(() => source.SearchAsync(AnyQuery()));
    }

    [Fact]
    public async Task GetCapsAsync_On429_ThrowsRequestLimitReached()
    {
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.TooManyRequests)), new FakeCircuitBreaker());

        await Assert.ThrowsAsync<RequestLimitReachedException>(() => source.GetCapsAsync(SearchProtocol.Newznab));
    }

    [Fact]
    public async Task SearchAsync_On301_ThrowsUpstreamRedirectRefused()
    {
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.MovedPermanently)), new FakeCircuitBreaker());

        await Assert.ThrowsAsync<UpstreamRedirectRefusedException>(() => source.SearchAsync(AnyQuery()));
    }

    /// <summary>
    /// 304 is refused like any other 3xx: the request sends no conditional headers, so a 304 cannot
    /// legitimately arise, and treating it as a non-refusal would let an upstream answer a search
    /// with no body at all and have it read as an empty result set.
    /// </summary>
    [Fact]
    public async Task SearchAsync_On304_ThrowsUpstreamRedirectRefused()
    {
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.NotModified)), new FakeCircuitBreaker());

        await Assert.ThrowsAsync<UpstreamRedirectRefusedException>(() => source.SearchAsync(AnyQuery()));
    }

    [Fact]
    public async Task GetCapsAsync_On304_ThrowsUpstreamRedirectRefused()
    {
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.NotModified)), new FakeCircuitBreaker());

        await Assert.ThrowsAsync<UpstreamRedirectRefusedException>(() => source.GetCapsAsync(SearchProtocol.Newznab));
    }

    /// <summary>
    /// A refused redirect records a breaker SUCCESS, not a failure: the upstream answered promptly,
    /// and a probe that recorded neither would strand a HalfOpen breaker, refusing every caller.
    /// </summary>
    [Fact]
    public async Task SearchAsync_OnARefusedRedirect_RecordsNoBreakerFailure()
    {
        var breaker = new FakeCircuitBreaker();
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.Found)), breaker);

        await Assert.ThrowsAsync<UpstreamRedirectRefusedException>(() => source.SearchAsync(AnyQuery()));

        Assert.Empty(breaker.Failures);
    }

    [Fact]
    public async Task SearchAsync_OnARefusedRedirect_RecordsABreakerSuccess()
    {
        var breaker = new FakeCircuitBreaker();
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.Found)), breaker);

        await Assert.ThrowsAsync<UpstreamRedirectRefusedException>(() => source.SearchAsync(AnyQuery()));

        Assert.Equal(1, breaker.SuccessCount);
    }

    [Fact]
    public async Task SearchAsync_WhenTheBreakerIsOpen_ReturnsEmptyWithoutCallingUpstream()
    {
        var handler = Answering(HttpStatusCode.OK, OneItemFeed);
        var breaker = new FakeCircuitBreaker();
        breaker.SetCanCall(false);
        var source = new NewznabSource(MakeOptions(), new HttpClient(handler), breaker);

        await source.SearchAsync(AnyQuery());

        Assert.Empty(handler.RequestedUris);
    }

    // ---------------------------------------------------------------------
    // Per-source timeout
    // ---------------------------------------------------------------------

    [Fact]
    public void Constructor_AppliesThePerSourceTimeoutOverrideToTheClient()
    {
        var client = new HttpClient(Answering(HttpStatusCode.OK));

        _ = new NewznabSource(MakeOptions(requestTimeout: TimeSpan.FromSeconds(37)), client, new FakeCircuitBreaker());

        Assert.Equal(TimeSpan.FromSeconds(37), client.Timeout);
    }

    /// <summary>
    /// A null TimeoutSeconds means the global default, NOT "no timeout" — a source that was never
    /// tuned must behave exactly like one whose operator explicitly chose the default.
    /// </summary>
    [Fact]
    public void Constructor_WithNoTimeoutOverride_AppliesTheDefaultRatherThanNoTimeout()
    {
        var client = new HttpClient(Answering(HttpStatusCode.OK));
        var options = new NewznabSourceOptions(
            BaseUrl: new Uri("http://indexer.example:9117/"),
            ApiPath: "/api",
            ApiKey: "test-api-key",
            SourceName: "test-indexer",
            RequestTimeout: null,
            RateLimitMaxCalls: 1000,
            RateLimitInterval: TimeSpan.FromMilliseconds(1));

        _ = new NewznabSource(options, client, new FakeCircuitBreaker());

        Assert.Equal(NewznabSourceOptions.DefaultRequestTimeout, client.Timeout);
    }

    /// <summary>
    /// Downloads are arb-x7w8.13/.14, not this adapter. The refusal is asserted so the unimplemented
    /// path fails loudly rather than being reachable and silently bypassing the access-mode
    /// decision that bead exists to make.
    /// </summary>
    [Fact]
    public async Task FetchDownloadAsync_IsRefused()
    {
        var source = new NewznabSource(MakeOptions(), new HttpClient(Answering(HttpStatusCode.OK)), new FakeCircuitBreaker());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => source.FetchDownloadAsync(new Arbitarr.Core.Releases.ReleaseCandidate
            {
                Title = "Example",
                Guid = "guid-1",
                PubDate = DateTimeOffset.UnixEpoch,
                Link = new Uri("http://indexer.example:9117/download/1"),
            }));
    }
}
