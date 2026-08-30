using Arbitarr.Api.Rendering;
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
    public void Torznab_search_results_render_exact_whole_document()
    {
        // Whole-document golden test (closes M1-3): exact bytes for a two-item torznab response
        // including seeders/peers/infohash attrs, modulo a single trailing newline. The proxy
        // guid is keyed by a per-process HMAC secret (SEC-L2) and so is not itself a literal —
        // it is interpolated from the same release/ProxyGuid the production writer consumes,
        // while every other byte of the document is an inline literal.
        var releases = new[] { TestReleases.Torrent(), TestReleases.Torrent(sourceName: "second-source", guid: "999", title: "Second.Release.1080p") };
        var xml = TorznabXmlWriter.WriteSearchResults(releases, r => new Uri($"http://localhost/download/{r.ProxyGuid}"));
        var rendered = XmlDocumentRendering_ToXmlString(xml) + Environment.NewLine;

        var expected =
            "<rss version=\"2.0\" xmlns:atom=\"http://www.w3.org/2005/Atom\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\">\r\n" +
            "  <channel>\r\n" +
            "    <title>Arbitarr</title>\r\n" +
            "    <description>Arbitarr merged search results</description>\r\n" +
            "    <item>\r\n" +
            "      <title>Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB</title>\r\n" +
            $"      <guid isPermaLink=\"false\">{releases[0].ProxyGuid}</guid>\r\n" +
            $"      <link>http://localhost/download/{releases[0].ProxyGuid}</link>\r\n" +
            "      <pubDate>Sun, 23 Aug 2026 10:55:15 GMT</pubDate>\r\n" +
            "      <size>1138166333</size>\r\n" +
            $"      <enclosure url=\"http://localhost/download/{releases[0].ProxyGuid}\" length=\"1138166333\" type=\"application/x-bittorrent\" />\r\n" +
            "      <torznab:attr name=\"category\" value=\"5000\" />\r\n" +
            "      <torznab:attr name=\"size\" value=\"1138166333\" />\r\n" +
            "      <torznab:attr name=\"protocol\" value=\"torrent\" />\r\n" +
            "      <torznab:attr name=\"infohash\" value=\"332afa1fd16fc0a5fd8d54e18d62e57f60a06764\" />\r\n" +
            "      <torznab:attr name=\"seeders\" value=\"182\" />\r\n" +
            "      <torznab:attr name=\"peers\" value=\"182\" />\r\n" +
            "    </item>\r\n" +
            "    <item>\r\n" +
            "      <title>Second.Release.1080p</title>\r\n" +
            $"      <guid isPermaLink=\"false\">{releases[1].ProxyGuid}</guid>\r\n" +
            $"      <link>http://localhost/download/{releases[1].ProxyGuid}</link>\r\n" +
            "      <pubDate>Sun, 23 Aug 2026 10:55:15 GMT</pubDate>\r\n" +
            "      <size>1138166333</size>\r\n" +
            $"      <enclosure url=\"http://localhost/download/{releases[1].ProxyGuid}\" length=\"1138166333\" type=\"application/x-bittorrent\" />\r\n" +
            "      <torznab:attr name=\"category\" value=\"5000\" />\r\n" +
            "      <torznab:attr name=\"size\" value=\"1138166333\" />\r\n" +
            "      <torznab:attr name=\"protocol\" value=\"torrent\" />\r\n" +
            "      <torznab:attr name=\"infohash\" value=\"332afa1fd16fc0a5fd8d54e18d62e57f60a06764\" />\r\n" +
            "      <torznab:attr name=\"seeders\" value=\"182\" />\r\n" +
            "      <torznab:attr name=\"peers\" value=\"182\" />\r\n" +
            "    </item>\r\n" +
            "  </channel>\r\n" +
            "</rss>\r\n";

        Assert.Equal(expected, rendered);
    }

    // XmlDocumentRendering is internal to Arbitarr.Api; reflect its known-stable ToXmlString
    // behavior here via the public writer facades plus XDocument's own ToString for assertions
    // that don't require the XML declaration.
    private static string XmlDocumentRendering_ToXmlString(System.Xml.Linq.XDocument document) => document.ToString();
}
