using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Golden-XML tests for search-result rendering (M1-4): pure passthrough of title/size/
/// category/guid, correct family-specific namespace prefix and enclosure MIME type.
/// </summary>
public class SearchXmlGoldenTests
{
    [Fact]
    public void Torznab_search_results_render_expected_namespace_and_enclosure()
    {
        var release = TestReleases.Torrent();
        var xml = TorznabXmlWriter.WriteSearchResults(new[] { release }, r => new Uri($"http://localhost/download/{r.ProxyGuid}"));
        var rendered = XmlDocumentRendering_ToXmlString(xml);

        Assert.Contains("xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"", rendered);
        Assert.Contains("type=\"application/x-bittorrent\"", rendered);
        Assert.Contains("<title>Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB</title>", rendered);
        Assert.Contains("<size>1138166333</size>", rendered);
        Assert.Contains("<torznab:attr name=\"category\" value=\"5000\" />", rendered);
        Assert.Contains($"<guid isPermaLink=\"false\">{release.ProxyGuid}</guid>", rendered);
    }

    [Fact]
    public void Newznab_search_results_render_expected_namespace_and_enclosure()
    {
        var release = TestReleases.Usenet();
        var xml = NewznabXmlWriter.WriteSearchResults(new[] { release }, r => new Uri($"http://localhost/download/{r.ProxyGuid}"));
        var rendered = XmlDocumentRendering_ToXmlString(xml);

        Assert.Contains("xmlns:newznab=\"http://torznab.com/schemas/2015/feed\"", rendered);
        Assert.Contains("type=\"application/x-nzb\"", rendered);
        Assert.Contains("<title>Some.Album.2026.FLAC</title>", rendered);
        Assert.Contains("<newznab:attr name=\"category\" value=\"3000\" />", rendered);
    }

    [Fact]
    public void Title_is_rendered_byte_exact_with_no_normalization()
    {
        var release = TestReleases.Torrent(title: "Weird.Title[Group]  Extra Spaces");
        var xml = TorznabXmlWriter.WriteSearchResults(new[] { release }, r => new Uri("http://localhost/download/x"));
        var rendered = XmlDocumentRendering_ToXmlString(xml);

        Assert.Contains("<title>Weird.Title[Group]  Extra Spaces</title>", rendered);
    }

    [Fact]
    public void Empty_result_set_renders_channel_with_no_items()
    {
        var xml = TorznabXmlWriter.WriteSearchResults(Array.Empty<RenderedRelease>(), r => new Uri("http://localhost/download/x"));
        var rendered = XmlDocumentRendering_ToXmlString(xml);

        Assert.DoesNotContain("<item>", rendered);
        Assert.Contains("<channel>", rendered);
    }

    [Fact]
    public async Task Torznab_search_results_render_exact_whole_document()
    {
        // Whole-document golden test (closes M1-3): exact bytes for a two-item torznab response
        // including seeders/peers/infohash attrs, rendered through the real production path
        // (SearchEndpoint.HandleTorznabAsync -> XmlDocumentRendering.ToXmlString), not a decoy
        // XDocument.ToString(). The proxy guid is keyed by a per-process HMAC secret (SEC-L2) and
        // so is not itself a literal — it is interpolated from the actual rendered body, while
        // every other byte of the document is an inline literal. Newlines are "\n" only: the
        // production renderer pins NewLineChars explicitly so the wire format is byte-identical
        // regardless of the host OS (this is itself a regression test for that bug).
        var firstRelease = TestReleases.Torrent();
        var secondRelease = TestReleases.Torrent(sourceName: "second-source", guid: "999", title: "Second.Release.1080p");
        var source = new FakeUpstreamSource("eztv", searchResults: new[] { firstRelease.Candidate, secondRelease.Candidate });
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(TestReleases.FixedPubDate);
        var snapshotService = new PaginationSnapshotService(mergeStage, store, time);
        var releaseLookup = new InMemoryReleaseLookup();

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost");

        var result = await SearchEndpoint.HandleTorznabAsync(
            "search",
            null,
            Array.Empty<int>(),
            50,
            0,
            "caller-api-key",
            snapshotService,
            releaseLookup,
            new RecentSearchLog(),
            httpContext.Request,
            CancellationToken.None);

        using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteAsync(httpContext);
        body.Seek(0, SeekOrigin.Begin);
        var rendered = new StreamReader(body).ReadToEnd();

        // Recover the proxy guids and download links the production renderer actually emitted,
        // the same way DownloadLinkPerClientTests does — they are HMAC-derived, not literals.
        var firstGuid = ExtractBetween(rendered, "<guid isPermaLink=\"false\">", "</guid>");
        var secondGuid = ExtractBetween(rendered, "<guid isPermaLink=\"false\">", "</guid>", occurrence: 2);

        var expected =
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n" +
            "<rss version=\"2.0\" xmlns:atom=\"http://www.w3.org/2005/Atom\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\">\n" +
            "  <channel>\n" +
            "    <title>Arbitarr</title>\n" +
            "    <description>Arbitarr merged search results</description>\n" +
            "    <item>\n" +
            "      <title>Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB</title>\n" +
            $"      <guid isPermaLink=\"false\">{firstGuid}</guid>\n" +
            $"      <link>http://localhost/download/{Uri.EscapeDataString(firstGuid)}?apikey=caller-api-key</link>\n" +
            "      <pubDate>Sun, 23 Aug 2026 10:55:15 GMT</pubDate>\n" +
            "      <size>1138166333</size>\n" +
            $"      <enclosure url=\"http://localhost/download/{Uri.EscapeDataString(firstGuid)}?apikey=caller-api-key\" length=\"1138166333\" type=\"application/x-bittorrent\" />\n" +
            "      <torznab:attr name=\"category\" value=\"5000\" />\n" +
            "      <torznab:attr name=\"size\" value=\"1138166333\" />\n" +
            "      <torznab:attr name=\"protocol\" value=\"torrent\" />\n" +
            "      <torznab:attr name=\"infohash\" value=\"332afa1fd16fc0a5fd8d54e18d62e57f60a06764\" />\n" +
            "      <torznab:attr name=\"seeders\" value=\"182\" />\n" +
            "      <torznab:attr name=\"peers\" value=\"182\" />\n" +
            "    </item>\n" +
            "    <item>\n" +
            "      <title>Second.Release.1080p</title>\n" +
            $"      <guid isPermaLink=\"false\">{secondGuid}</guid>\n" +
            $"      <link>http://localhost/download/{Uri.EscapeDataString(secondGuid)}?apikey=caller-api-key</link>\n" +
            "      <pubDate>Sun, 23 Aug 2026 10:55:15 GMT</pubDate>\n" +
            "      <size>1138166333</size>\n" +
            $"      <enclosure url=\"http://localhost/download/{Uri.EscapeDataString(secondGuid)}?apikey=caller-api-key\" length=\"1138166333\" type=\"application/x-bittorrent\" />\n" +
            "      <torznab:attr name=\"category\" value=\"5000\" />\n" +
            "      <torznab:attr name=\"size\" value=\"1138166333\" />\n" +
            "      <torznab:attr name=\"protocol\" value=\"torrent\" />\n" +
            "      <torznab:attr name=\"infohash\" value=\"332afa1fd16fc0a5fd8d54e18d62e57f60a06764\" />\n" +
            "      <torznab:attr name=\"seeders\" value=\"182\" />\n" +
            "      <torznab:attr name=\"peers\" value=\"182\" />\n" +
            "    </item>\n" +
            "  </channel>\n" +
            "</rss>";

        Assert.Equal(expected, rendered);
    }

    private static string ExtractBetween(string source, string start, string end, int occurrence = 1)
    {
        var searchFrom = 0;
        var startIndex = -1;
        for (var i = 0; i < occurrence; i++)
        {
            startIndex = source.IndexOf(start, searchFrom, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                throw new InvalidOperationException($"Occurrence {occurrence} of '{start}' not found.");
            }

            searchFrom = startIndex + start.Length;
        }

        startIndex += start.Length;
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        return source[startIndex..endIndex];
    }

    // XmlDocumentRendering is internal to Arbitarr.Api; the non-whole-document tests above only
    // assert substrings, so XDocument's own ToString() (which omits the XML declaration) is
    // sufficient for them. The whole-document test above routes through the real production
    // endpoint/renderer instead.
    private static string XmlDocumentRendering_ToXmlString(System.Xml.Linq.XDocument document) => document.ToString();
}
