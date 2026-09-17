using System.Xml.Linq;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-x7w8.15: the magnet admission (<see cref="TorznabFeedParser.TryValidateMagnetLink"/>) and the
/// parse-path branch that consults it.
///
/// <para>This is SEC-M1 code. The admission exists because a magnet is HOSTLESS, so the origin pin
/// refuses it and the item was dropped — silent, item-level data loss for a magnet-only tracker. The
/// fix admits the magnet scheme in its own branch rather than loosening
/// <see cref="UpstreamOrigin.IsAtOrigin"/>, which is shared with the write boundary AND stands in
/// front of every adapter's outbound fetch. The tests below are therefore written to fail if the
/// scheme check is ever widened: each negative names a scheme that must stay refused, and
/// <see cref="The_admission_refuses_an_http_link_that_merely_mentions_magnet_in_a_fragment"/> pins
/// the reason the check matches the PARSED SCHEME rather than a raw string prefix.</para>
/// </summary>
public sealed class MagnetLinkAdmissionTests
{
    private const string InfoHash = "332afa1fd16fc0a5fd8d54e18d62e57f60a06764";

    private static string Magnet(string? hash = null) =>
        $"magnet:?xt=urn:btih:{hash ?? InfoHash}&dn=Some.Release.1080p";

    /// <summary>The positive control: without it, an admission that returned false unconditionally
    /// would satisfy every refusal below while delivering nothing.</summary>
    [Fact]
    public void A_magnet_carrying_a_btih_exact_topic_is_admitted()
    {
        var admitted = TorznabFeedParser.TryValidateMagnetLink(Magnet(), out var validated);

        Assert.True(admitted);
        Assert.Equal("magnet", validated.Scheme);

        // The info hash survives verbatim: the route redirects to the link the FEED gave, and does
        // not synthesise one from ReleaseCandidate.InfoHash (which would drop the trackers).
        Assert.Contains(InfoHash, validated.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scheme is case-insensitive per RFC 3986, and the comparison is ordinal-ignore-case rather
    /// than culture-sensitive — a culture-sensitive compare on a security decision is the Turkish-I
    /// hazard.
    /// </summary>
    [Theory]
    [InlineData("MAGNET:?xt=urn:btih:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    [InlineData("MaGnEt:?xt=URN:BTIH:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    public void The_magnet_scheme_and_exact_topic_are_matched_case_insensitively(string link)
    {
        Assert.True(TorznabFeedParser.TryValidateMagnetLink(link, out _));
    }

    /// <summary>
    /// A magnet with NO BitTorrent exact topic is refused. Admission is "magnet AND btih", so an
    /// implementation that dropped the info-hash requirement and admitted the scheme alone fails
    /// here.
    /// </summary>
    [Theory]
    [InlineData("magnet:")]
    [InlineData("magnet:?dn=Some.Release.1080p")]
    [InlineData("magnet:?xt=urn:sha1:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    [InlineData("magnet:?xtra=urn:btih:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    public void A_magnet_without_a_btih_exact_topic_is_refused(string link)
    {
        Assert.False(TorznabFeedParser.TryValidateMagnetLink(link, out var validated));
        Assert.Null(validated);
    }

    /// <summary>
    /// Every other non-http(s) scheme stays refused exactly as before. These are the schemes a
    /// loosened check would admit, so this is the assertion that bites if the admission is ever
    /// rewritten as "not http(s), therefore non-fetchable, therefore fine".
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript:?xt=urn:btih:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://host/share?xt=urn:btih:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    [InlineData("ftp://indexer.example/pub/file.torrent")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void Every_other_non_http_scheme_stays_refused(string link)
    {
        Assert.False(TorznabFeedParser.TryValidateMagnetLink(link, out var validated));
        Assert.Null(validated);
    }

    /// <summary>
    /// Why the check matches the parsed SCHEME and not a raw-string prefix or a "contains magnet:"
    /// test. This value is an ordinary https URL whose FRAGMENT mentions a magnet; a substring test
    /// would admit it as non-fetchable and thereby hand an arbitrary origin a bypass around the
    /// origin pin.
    /// </summary>
    [Fact]
    public void The_admission_refuses_an_http_link_that_merely_mentions_magnet_in_a_fragment()
    {
        const string Link = "https://evil.example/x#magnet:?xt=urn:btih:332afa1fd16fc0a5fd8d54e18d62e57f60a06764";

        Assert.False(TorznabFeedParser.TryValidateMagnetLink(Link, out _));

        // ...and being http(s), it is judged by the origin pin, which refuses it against a different
        // origin exactly as it always did. The admission changed nothing for http(s) links.
        Assert.False(TorznabFeedParser.TryValidateOriginPinnedLink(
            Link,
            new Uri("https://indexer.example:9117"),
            out _));
    }

    /// <summary>
    /// The pin is UNCHANGED: it still refuses a magnet. This is the property that keeps the
    /// fetch-time SSRF gate intact — every adapter's <c>FetchDownloadAsync</c> calls the pin, not
    /// the admission, immediately before issuing an HTTP request. If a future change routed the
    /// admission into the pin, this fails.
    /// </summary>
    [Fact]
    public void The_origin_pin_still_refuses_a_magnet_so_the_fetch_time_gate_is_untouched()
    {
        Assert.False(TorznabFeedParser.TryValidateOriginPinnedLink(
            Magnet(),
            new Uri("https://indexer.example:9117"),
            out var validated));
        Assert.Null(validated);
    }

    /// <summary>
    /// At the PARSER level, per item: the unit assertions above prove what the admission returns,
    /// not that <see cref="TorznabFeedParser.ParseFeedResponse"/> consults it. Asserted per item —
    /// "some item survived" would still pass against an implementation that kept the javascript one.
    /// </summary>
    [Fact]
    public void ParseFeedResponse_keeps_the_magnet_and_the_pinned_item_and_drops_the_rest()
    {
        var feed = BuildFeed(
            ("Magnet Release", Magnet(), null),
            ("Pinned Release", "https://indexer.example:9117/dl?id=1", null),
            ("Script Release", "javascript:alert(1)", null),
            ("Foreign Release", "https://evil.example:9117/dl?id=2", null),
            ("Hashless Magnet", "magnet:?dn=Some.Release.1080p", null));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        var titles = results.Select(r => r.Title).ToArray();
        Assert.Contains("Magnet Release", titles);
        Assert.Contains("Pinned Release", titles);
        Assert.DoesNotContain("Script Release", titles);
        Assert.DoesNotContain("Foreign Release", titles);
        Assert.DoesNotContain("Hashless Magnet", titles);
        Assert.Equal(2, results.Count);
    }

    /// <summary>
    /// The mislabelling the magnet arm in the protocol switch exists to prevent: this parser's
    /// fallback defaults a protocol-silent item to Usenet, so a magnet-bearing item from a feed that
    /// omits the attribute would be reported as Usenet to every ranking and rendering surface.
    /// Asserted beside a protocol-silent HTTP item, which must still default to Usenet — otherwise
    /// this would also pass against an implementation that made everything a Torrent.
    /// </summary>
    [Fact]
    public void A_protocol_silent_magnet_item_is_a_torrent_while_a_protocol_silent_http_item_stays_usenet()
    {
        var feed = BuildFeed(
            ("Magnet Release", Magnet(), null),
            ("Pinned Release", "https://indexer.example:9117/dl?id=1", null));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        Assert.Equal(ProtocolKind.Torrent, Assert.Single(results, r => r.Title == "Magnet Release").Protocol);
        Assert.Equal(ProtocolKind.Usenet, Assert.Single(results, r => r.Title == "Pinned Release").Protocol);
    }

    /// <summary>
    /// An explicit <c>protocol</c> attribute still wins over the link, because the download route
    /// detects a magnet on the LINK and never on this value — so honouring the attribute here cannot
    /// cause a magnet to be fetched. Pins that the magnet arm was added to the fallback rather than
    /// ahead of the declared attribute.
    /// </summary>
    [Fact]
    public void An_explicit_protocol_attribute_still_wins_over_the_magnet_link()
    {
        var feed = BuildFeed(("Declared Usenet", Magnet(), "usenet"));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        Assert.Equal(ProtocolKind.Usenet, Assert.Single(results).Protocol);
    }

    private static string BuildFeed(params (string Title, string Link, string? Protocol)[] items)
    {
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";

        var rss = new XElement("rss",
            new XAttribute(XNamespace.Xmlns + "torznab", torznab),
            new XElement("channel",
                items.Select(item => new XElement("item",
                    new XElement("title", item.Title),
                    new XElement("guid", item.Title),
                    new XElement("link", item.Link),
                    new XElement("size", "1024"),
                    item.Protocol is null
                        ? null
                        : new XElement(torznab + "attr",
                            new XAttribute("name", "protocol"),
                            new XAttribute("value", item.Protocol))))));

        return new XDocument(rss).ToString();
    }
}
