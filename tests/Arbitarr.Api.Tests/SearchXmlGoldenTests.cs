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

    // XmlDocumentRendering is internal to Arbitarr.Api; reflect its known-stable ToXmlString
    // behavior here via the public writer facades plus XDocument's own ToString for assertions
    // that don't require the XML declaration.
    private static string XmlDocumentRendering_ToXmlString(System.Xml.Linq.XDocument document) => document.ToString();
}
