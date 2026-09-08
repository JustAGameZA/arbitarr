using System.Net;
using System.Text;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Sources.NzbHydra.Tests;

/// <summary>
/// #99: the upstream endpoint and search type must be derived from the inbound request, not
/// hardcoded. Before this, every search went to <c>{base}/torznab/api</c> with <c>t=search</c>,
/// which NZBHydra2 reads as a torrent search — it drops every usenet indexer from the selection, so
/// a Newznab caller got zero usenet results from a request that still looked successful.
///
/// <para>
/// Every assertion here is made PER QUERY PARAMETER against a parsed query string, never as a
/// substring of the URL. A substring check (<c>Assert.Contains("t=tvsearch", uri)</c>) passes on a
/// URL that also carries a second, contradictory <c>t=</c>, and <c>Assert.Contains("/api", uri)</c>
/// is satisfied by <c>/torznab/api</c> — which is precisely the bug this file exists to catch. The
/// path is likewise compared with an exact equality against <c>AbsolutePath</c>.
/// </para>
/// </summary>
public class NzbHydraUpstreamProtocolTests
{
    private const string ApiKey = "secret-api-key";

    private static NzbHydraSourceOptions MakeOptions() => new(
        BaseUrl: new Uri("http://hydra.example.invalid:5076/"),
        ApiKey: ApiKey,
        SourceName: "test-hydra",
        RequestTimeout: TimeSpan.FromSeconds(2),
        MaxUpstreamPageSize: 100,
        MaxUpstreamCallsPerSearch: 3,
        RateLimitMaxCalls: 1000,
        RateLimitInterval: TimeSpan.FromMilliseconds(1));

    private static string EmptyFeed() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + "<rss xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel></channel></rss>";

    private static Dictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);

    private static (FakeHttpMessageHandler Handler, NzbHydraSource Source) MakeSource(string? body = null)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body ?? EmptyFeed(), Encoding.UTF8, "application/xml"),
        });
        return (handler, new NzbHydraSource(MakeOptions(), new HttpClient(handler), new FakeCircuitBreaker()));
    }

    // ---- AC1: the endpoint follows the inbound protocol --------------------------------------

    [Theory]
    [InlineData(SearchProtocol.Torznab, "/torznab/api")]
    [InlineData(SearchProtocol.Newznab, "/api")]
    public async Task SearchAsync_IssuesTheRequest_AgainstTheEndpointForTheInboundProtocol(
        SearchProtocol protocol,
        string expectedPath)
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), 10, protocol));

        var uri = Assert.Single(handler.RequestedUris);
        Assert.Equal(expectedPath, uri.AbsolutePath);
        Assert.Equal("hydra.example.invalid", uri.Host);
        Assert.Equal(5076, uri.Port);
    }

    [Theory]
    [InlineData(SearchProtocol.Torznab, "/torznab/api")]
    [InlineData(SearchProtocol.Newznab, "/api")]
    public async Task GetCapsAsync_IssuesTheRequest_AgainstTheEndpointForTheInboundProtocol(
        SearchProtocol protocol,
        string expectedPath)
    {
        var (handler, source) = MakeSource("<?xml version=\"1.0\"?><caps><categories /></caps>");

        await source.GetCapsAsync(protocol);

        var uri = Assert.Single(handler.RequestedUris);
        Assert.Equal(expectedPath, uri.AbsolutePath);
        Assert.Equal("caps", ParseQuery(uri)["t"]);
    }

    /// <summary>
    /// The regression the issue reports verbatim: a Newznab caller must not be routed to the
    /// torrent-only endpoint. Asserted as an inequality on the exact path so it cannot be satisfied
    /// by a URL that merely CONTAINS "/api".
    /// </summary>
    [Fact]
    public async Task SearchAsync_ForANewznabCaller_NeverTouchesTheTorrentOnlyTorznabEndpoint()
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), 10, SearchProtocol.Newznab));

        var uri = Assert.Single(handler.RequestedUris);
        Assert.NotEqual("/torznab/api", uri.AbsolutePath);
        Assert.Equal("/api", uri.AbsolutePath);
    }

    /// <summary>
    /// The endpoint choice must survive fan-out: a >100-result request issues several upstream
    /// calls, and EVERY one of them has to land on the caller's endpoint. Asserting only the first
    /// would miss a per-page rebuild that reverted to the hardcoded path (AC6 keeps the fan-out
    /// logic itself unchanged).
    /// </summary>
    [Fact]
    public async Task SearchAsync_WhenFanningOut_EveryPagedRequestUsesTheSameProtocolEndpoint()
    {
        var handler = new FakeHttpMessageHandler(request =>
        {
            var offset = int.Parse(ParseQuery(request.RequestUri!)["offset"]);
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<rss xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>");
            for (var i = 0; i < 100; i++)
            {
                sb.Append("<item><title>R")
                  .Append(offset + i)
                  .Append("</title><guid>g")
                  .Append(offset + i)
                  .Append("</guid><link>http://hydra.example.invalid:5076/dl/")
                  .Append(offset + i)
                  .Append("</link></item>");
            }

            sb.Append("</channel></rss>");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/xml"),
            };
        });
        var source = new NzbHydraSource(MakeOptions(), new HttpClient(handler), new FakeCircuitBreaker());

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), 300, SearchProtocol.Newznab));

        Assert.Equal(3, handler.RequestedUris.Count);
        Assert.All(handler.RequestedUris, u => Assert.Equal("/api", u.AbsolutePath));
    }

    // ---- AC2: the search type and id parameters are forwarded ---------------------------------

    /// <summary>
    /// A tvdbid-bearing query becomes <c>t=tvsearch</c> with <c>tvdbid</c>/<c>season</c>/<c>ep</c>
    /// as their own parameters. Each is asserted individually, and <c>q</c> is asserted to still be
    /// the bare query term — the ids must never be folded into it.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithATvdbId_SendsTvSearchWithTheIdSeasonAndEpisodeAsSeparateParameters()
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery(
            "bleach", Array.Empty<int>(), 10, SearchProtocol.Newznab,
            TvdbId: 74796, Season: 17, Episode: 36));

        var query = ParseQuery(Assert.Single(handler.RequestedUris));
        Assert.Equal("tvsearch", query["t"]);
        Assert.Equal("74796", query["tvdbid"]);
        Assert.Equal("17", query["season"]);
        Assert.Equal("36", query["ep"]);
        Assert.Equal("bleach", query["q"]);
        Assert.False(query.ContainsKey("tmdbid"));
    }

    [Fact]
    public async Task SearchAsync_WithATmdbId_SendsMovieSearchWithTheIdAsItsOwnParameter()
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery(
            "dune", Array.Empty<int>(), 10, SearchProtocol.Newznab, TmdbId: 438631));

        var query = ParseQuery(Assert.Single(handler.RequestedUris));
        Assert.Equal("movie", query["t"]);
        Assert.Equal("438631", query["tmdbid"]);
        Assert.Equal("dune", query["q"]);
        Assert.False(query.ContainsKey("tvdbid"));
        Assert.False(query.ContainsKey("season"));
        Assert.False(query.ContainsKey("ep"));
    }

    [Fact]
    public async Task SearchAsync_WithNoIds_SendsAPlainSearchAndNoIdParameters()
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), 10, SearchProtocol.Torznab));

        var query = ParseQuery(Assert.Single(handler.RequestedUris));
        Assert.Equal("search", query["t"]);
        Assert.Equal("bleach", query["q"]);
        Assert.False(query.ContainsKey("tvdbid"));
        Assert.False(query.ContainsKey("tmdbid"));
        Assert.False(query.ContainsKey("season"));
        Assert.False(query.ContainsKey("ep"));
    }

    /// <summary>
    /// Season 0 (the specials season) and episode 0 are legitimate values that
    /// <c>IdParamClamp</c> deliberately admits — its season/episode clamps reject only NEGATIVE
    /// numbers, unlike the provider-id clamp which rejects zero. A truthiness-style check
    /// (<c>if (season > 0)</c>) would silently drop them, so this pins the nullability semantics
    /// rather than the value being non-zero.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithSeasonZero_StillSendsTheSeasonParameter()
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery(
            "bleach", Array.Empty<int>(), 10, SearchProtocol.Newznab,
            TvdbId: 74796, Season: 0, Episode: 0));

        var query = ParseQuery(Assert.Single(handler.RequestedUris));
        Assert.Equal("tvsearch", query["t"]);
        Assert.Equal("0", query["season"]);
        Assert.Equal("0", query["ep"]);
    }

    /// <summary>
    /// A tvdbid without season/ep is a whole-series search: the id goes up, the two numbering
    /// parameters must be absent rather than sent empty or as zero (either would narrow the search
    /// upstream to a season/episode the caller never asked for).
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithATvdbIdButNoNumbering_OmitsSeasonAndEpisodeEntirely()
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery(
            "bleach", Array.Empty<int>(), 10, SearchProtocol.Newznab, TvdbId: 74796));

        var query = ParseQuery(Assert.Single(handler.RequestedUris));
        Assert.Equal("tvsearch", query["t"]);
        Assert.Equal("74796", query["tvdbid"]);
        Assert.False(query.ContainsKey("season"));
        Assert.False(query.ContainsKey("ep"));
    }

    /// <summary>
    /// The categories and paging parameters the pre-#99 code already sent must be unaffected by the
    /// new mode selection — asserted per parameter alongside it.
    /// </summary>
    [Fact]
    public async Task SearchAsync_StillSendsCategoriesAndPaging_AlongsideTheNewSearchType()
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery(
            "bleach", new[] { 5030, 5040 }, 25, SearchProtocol.Newznab, Offset: 50, TvdbId: 74796));

        var query = ParseQuery(Assert.Single(handler.RequestedUris));
        Assert.Equal("tvsearch", query["t"]);
        Assert.Equal("5030,5040", query["cat"]);
        Assert.Equal("25", query["limit"]);
        Assert.Equal("50", query["offset"]);
    }

    // ---- AC4: the apikey stays in the query string ---------------------------------------------

    /// <summary>
    /// CLAUDE.md §1: <c>IHttpClientFactory</c> attaches its own logging handler to every named
    /// client and logs the full absolute URI at Information, and <c>LogMessageCleanser</c> scrubs
    /// credentials in QUERY STRINGS only. A key moved into the URL path would therefore be logged
    /// verbatim and never redacted. This pins the key to the query string on BOTH endpoints, so the
    /// #99 path change cannot be the edit that quietly relocates it.
    /// </summary>
    [Theory]
    [InlineData(SearchProtocol.Torznab)]
    [InlineData(SearchProtocol.Newznab)]
    public async Task SearchAsync_KeepsTheApiKeyInTheQueryString_AndOutOfThePath(SearchProtocol protocol)
    {
        var (handler, source) = MakeSource();

        await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), 10, protocol));

        var uri = Assert.Single(handler.RequestedUris);
        Assert.Equal(ApiKey, ParseQuery(uri)["apikey"]);
        Assert.DoesNotContain(ApiKey, uri.AbsolutePath, StringComparison.Ordinal);
    }

    // ---- AC3: origin pinning holds for both endpoints -----------------------------------------

    /// <summary>
    /// The SSRF origin pin must keep working now that results arrive from two different endpoints.
    /// A usenet link and a torrent link from the configured origin are both kept; a third item
    /// pointing at a foreign origin is dropped from the same feed.
    ///
    /// <para>
    /// The two kept items are the POSITIVE CONTROL for the dropped one: asserting only that the
    /// foreign link is absent would pass just as happily against a parser that dropped every item,
    /// or returned nothing at all. Asserting that exactly the two same-origin items survive proves
    /// the filter discriminates rather than merely excludes.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(SearchProtocol.Torznab)]
    [InlineData(SearchProtocol.Newznab)]
    public async Task SearchAsync_OriginPinsUsenetAndTorrentLinksAlike_AndDropsAForeignOrigin(
        SearchProtocol protocol)
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
            <item>
              <title>Usenet release</title>
              <guid>guid-usenet</guid>
              <link>http://hydra.example.invalid:5076/getnzb/usenet-1</link>
              <torznab:attr name="protocol" value="usenet" />
            </item>
            <item>
              <title>Torrent release</title>
              <guid>guid-torrent</guid>
              <link>http://hydra.example.invalid:5076/gettorrent/torrent-1</link>
              <torznab:attr name="protocol" value="torrent" />
            </item>
            <item>
              <title>Foreign origin</title>
              <guid>guid-foreign</guid>
              <link>http://192.0.2.99:9999/internal/secrets</link>
              <torznab:attr name="protocol" value="usenet" />
            </item>
            </channel></rss>
            """;
        var (handler, source) = MakeSource(xml);

        var results = await source.SearchAsync(new SearchQuery("bleach", Array.Empty<int>(), 10, protocol));

        // Positive control: the two same-origin items survive, one of each protocol.
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Protocol == ProtocolKind.Usenet && r.Title == "Usenet release");
        Assert.Contains(results, r => r.Protocol == ProtocolKind.Torrent && r.Title == "Torrent release");

        // And the foreign-origin one is gone.
        Assert.DoesNotContain(results, r => r.Title == "Foreign origin");
        Assert.All(results, r => Assert.Equal("hydra.example.invalid", r.Link!.Host));
    }

    /// <summary>
    /// Fetch-time re-validation is endpoint-independent: a usenet link from the configured origin
    /// is fetched, while a foreign-origin one is refused before any HTTP call is made. The first
    /// half is the positive control — without it, "no request was made" would also be true of a
    /// method that refused everything.
    /// </summary>
    [Fact]
    public async Task FetchDownloadAsync_FetchesASameOriginUsenetLink_ButRefusesAForeignOne()
    {
        var (handler, source) = MakeSource("nzb-bytes");

        var allowed = new ReleaseCandidate
        {
            Title = "Usenet release",
            Guid = "guid-usenet",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("http://hydra.example.invalid:5076/getnzb/usenet-1"),
            Protocol = ProtocolKind.Usenet,
        };

        await using (await source.FetchDownloadAsync(allowed))
        {
            var fetched = Assert.Single(handler.RequestedUris);
            Assert.Equal("/getnzb/usenet-1", fetched.AbsolutePath);
        }

        var foreign = new ReleaseCandidate
        {
            Title = allowed.Title,
            Guid = allowed.Guid,
            PubDate = allowed.PubDate,
            Link = new Uri("http://192.0.2.99:9999/internal/secrets"),
            Protocol = ProtocolKind.Usenet,
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchDownloadAsync(foreign));

        // Still just the one request: the refused fetch never reached the network.
        Assert.Single(handler.RequestedUris);
    }
}
