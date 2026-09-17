using System.Xml.Linq;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-4ysc: the end of the info-hash path — from the upstream feed's magnet through
/// <see cref="TorznabFeedParser.ParseFeedResponse"/> into <c>IndexerXmlWriter</c>'s
/// <c>torznab:attr name="infohash"</c>.
///
/// <para><b>Why this is a separate test from the parser's own.</b> The parser tests prove
/// <see cref="ReleaseCandidate.InfoHash"/> is populated; they cannot prove the writer emits it,
/// and the writer's branch had been unreachable from any production path since it was written.
/// Asserting both halves in one place is what makes the gap closed rather than merely narrowed —
/// a change that populated the field but renamed the attr would pass the parser tests alone.</para>
/// </summary>
public sealed class MagnetInfoHashRenderingTests
{
    private const string MagnetHash = "332afa1fd16fc0a5fd8d54e18d62e57f60a06764";
    private const string DeclaredHash = "0123456789abcdef0123456789abcdef01234567";

    private static readonly Uri AllowedOrigin = new("https://indexer.example:9117");

    /// <summary>
    /// The per-item assertion CLAUDE.md §4 requires, over ONE rendered document carrying both kinds.
    /// The magnet item's infohash attr is present and carries its own hash; the NZB item beside it
    /// has no infohash attr at all.
    ///
    /// <para>Both halves are in one test on purpose. An implementation that wrote one hash to every
    /// candidate — the shape an assignment hoisted out of the parse loop produces — satisfies the
    /// magnet assertion and fails the NZB one. Splitting them into two tests would let that
    /// implementation pass the first while the second was read as covering a different concern.</para>
    /// </summary>
    [Fact]
    public void A_magnet_item_renders_its_infohash_attr_while_an_nzb_item_in_the_same_feed_renders_none()
    {
        var rendered = RenderFeed(
            ("Magnet Release", $"magnet:?xt=urn:btih:{MagnetHash}&dn=Some.Release.1080p", null),
            ("Nzb Release", "https://indexer.example:9117/dl?id=1", null));

        var magnetItem = ItemNamed(rendered, "Magnet Release");
        var nzbItem = ItemNamed(rendered, "Nzb Release");

        Assert.Equal(MagnetHash, InfoHashAttrOf(magnetItem));
        Assert.Null(InfoHashAttrOf(nzbItem));
    }

    /// <summary>
    /// The positive control for the null assertion above, in both directions it can be vacuous.
    ///
    /// <para><b>Direction one — the attr must be emittable at all.</b> Before arb-4ysc this document
    /// would have rendered without a single infohash attr, because nothing set the field; "the NZB
    /// item has none" was then vacuously true of EVERY item, an empty set containing nothing. The
    /// first assertion pins that exactly one is emitted, for the one item that should have it.</para>
    ///
    /// <para><b>Direction two — the reader must be able to SEE one on an NZB item.</b> That the
    /// document carries a hash somewhere does not prove <c>InfoHashAttrOf(nzbItem)</c> would find one
    /// if it were there; a helper that looked at the wrong item, or matched the attr name with the
    /// wrong comparison, returns null for a reason that has nothing to do with the behaviour under
    /// test. The second assertion plants a hash on the NZB item via a declared attr and demonstrates
    /// the same reader returns it — so the null above is an observed absence, not a blind spot.</para>
    /// </summary>
    [Fact]
    public void The_rendered_document_carries_exactly_one_infohash_attr_for_the_one_magnet_item()
    {
        var rendered = RenderFeed(
            ("Magnet Release", $"magnet:?xt=urn:btih:{MagnetHash}&dn=Some.Release.1080p", null),
            ("Nzb Release", "https://indexer.example:9117/dl?id=1", null));

        var hashes = rendered
            .Descendants(IndexerXmlWriterSchemaNs + "attr")
            .Where(attr => string.Equals(attr.Attribute("name")?.Value, "infohash", StringComparison.Ordinal))
            .Select(attr => attr.Attribute("value")?.Value)
            .ToArray();

        Assert.Equal(new[] { MagnetHash }, hashes);

        // The planted control: the SAME NZB title, the SAME reader, but a hash the feed did supply.
        var withPlantedHash = RenderFeed(
            ("Magnet Release", $"magnet:?xt=urn:btih:{MagnetHash}&dn=Some.Release.1080p", null),
            ("Nzb Release", "https://indexer.example:9117/dl?id=1", DeclaredHash));

        Assert.Equal(DeclaredHash, InfoHashAttrOf(ItemNamed(withPlantedHash, "Nzb Release")));
    }

    /// <summary>
    /// The precedence rule survives rendering: a declared <c>infohash</c> attr on the upstream item
    /// is the value re-emitted, not the magnet's btih. Asserted with two DIFFERENT hashes, since
    /// equal ones would pass under either precedence.
    /// </summary>
    [Fact]
    public void A_declared_infohash_attr_is_the_value_rendered_rather_than_the_magnets_btih()
    {
        var rendered = RenderFeed(
            ("Both Disagree", $"magnet:?xt=urn:btih:{MagnetHash}&dn=Some.Release.1080p", DeclaredHash));

        Assert.Equal(DeclaredHash, InfoHashAttrOf(ItemNamed(rendered, "Both Disagree")));
    }

    /// <summary>
    /// A whitespace-only declared <c>infohash</c> attr is not a declared value: the magnet's btih is
    /// rendered instead. This pins <c>TorznabFeedParser</c>'s use of
    /// <c>string.IsNullOrWhiteSpace</c> rather than <c>string.IsNullOrEmpty</c> — a tidy-up to the
    /// latter would treat the whitespace run as "present" and blank a hash the magnet did supply.
    /// </summary>
    [Fact]
    public void A_whitespace_only_declared_infohash_attr_falls_back_to_the_magnets_btih()
    {
        var rendered = RenderFeed(
            ("Whitespace Attr", $"magnet:?xt=urn:btih:{MagnetHash}&dn=Some.Release.1080p", "   "));

        Assert.Equal(MagnetHash, InfoHashAttrOf(ItemNamed(rendered, "Whitespace Attr")));
    }

    private static readonly XNamespace IndexerXmlWriterSchemaNs = "http://torznab.com/schemas/2015/feed";

    private static XElement ItemNamed(XDocument rendered, string title) =>
        Assert.Single(
            rendered.Descendants("item"),
            item => string.Equals(item.Element("title")?.Value, title, StringComparison.Ordinal));

    private static string? InfoHashAttrOf(XElement item) =>
        item.Elements(IndexerXmlWriterSchemaNs + "attr")
            .FirstOrDefault(attr => string.Equals(attr.Attribute("name")?.Value, "infohash", StringComparison.Ordinal))
            ?.Attribute("value")?.Value;

    /// <summary>
    /// Builds an upstream feed, parses it through the real production parser, and renders the result
    /// through the real production writer. Going through both rather than constructing
    /// <see cref="ReleaseCandidate"/> by hand is the point: a hand-built candidate would assert the
    /// writer works, which was never in doubt, and not that the parser feeds it.
    /// </summary>
    private static XDocument RenderFeed(params (string Title, string Link, string? InfoHashAttr)[] items)
    {
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";

        var feed = new XDocument(new XElement("rss",
            new XAttribute(XNamespace.Xmlns + "torznab", torznab),
            new XElement("channel",
                items.Select(item => new XElement("item",
                    new XElement("title", item.Title),
                    new XElement("guid", item.Title),
                    new XElement("link", item.Link),
                    new XElement("size", "1024"),
                    item.InfoHashAttr is null
                        ? null
                        : new XElement(torznab + "attr",
                            new XAttribute("name", "infohash"),
                            new XAttribute("value", item.InfoHashAttr))))))).ToString();

        var candidates = TorznabFeedParser.ParseFeedResponse(feed, AllowedOrigin);

        var releases = candidates
            .Select(candidate => new RenderedRelease("testsrc", candidate))
            .ToArray();

        return TorznabXmlWriter.WriteSearchResults(
            releases,
            _ => new Uri("https://arbitarr.example/download/1"));
    }
}
