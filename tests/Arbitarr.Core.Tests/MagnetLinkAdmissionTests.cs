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
        var admitted = TorznabFeedParser.TryValidateMagnetLink(Magnet(), out var validated, out var infoHash);

        Assert.True(admitted);
        Assert.Equal("magnet", validated.Scheme);

        // The info hash survives verbatim: the route redirects to the link the FEED gave, and does
        // not synthesise one from ReleaseCandidate.InfoHash (which would drop the trackers).
        Assert.Contains(InfoHash, validated.OriginalString, StringComparison.Ordinal);

        // arb-4ysc: and the admission hands the btih back rather than discarding it, which is what
        // lets the parser populate ReleaseCandidate.InfoHash without parsing the link a second time.
        Assert.Equal(InfoHash, infoHash);
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
        Assert.True(TorznabFeedParser.TryValidateMagnetLink(link, out _, out _));
    }

    /// <summary>
    /// A magnet with NO BitTorrent exact topic is refused. Admission is "magnet AND btih", so an
    /// implementation that dropped the info-hash requirement and admitted the scheme alone fails
    /// here.
    ///
    /// <para>codereview-492: the last three cases are an EMPTY btih. <c>magnet:?xt=urn:btih:</c>
    /// satisfied the old prefix test while naming no torrent at all, so it was admitted and would
    /// now yield an empty info hash to render. The third of them pins that an empty btih does not
    /// mask a later one only when there IS no later one — the positive direction is
    /// <see cref="A_btih_in_a_second_xt_parameter_is_admitted_and_its_hash_returned"/>.</para>
    /// </summary>
    [Theory]
    [InlineData("magnet:")]
    [InlineData("magnet:?dn=Some.Release.1080p")]
    [InlineData("magnet:?xt=urn:sha1:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    [InlineData("magnet:?xtra=urn:btih:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    [InlineData("magnet:?xt=urn:btih:")]
    [InlineData("magnet:?xt=urn:btih:&dn=Some.Release.1080p")]
    [InlineData("magnet:?xt=urn:btih:&xt=urn:sha1:332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    public void A_magnet_without_a_btih_exact_topic_is_refused(string link)
    {
        Assert.False(TorznabFeedParser.TryValidateMagnetLink(link, out var validated, out var infoHash));
        Assert.Null(validated);
        Assert.Null(infoHash);
    }

    /// <summary>
    /// codereview-492: a btih in a SECOND <c>xt</c> parameter is admitted, and its hash is the one
    /// returned. A magnet may legitimately carry several exact topics in any order, so this is
    /// correct behaviour — but it was unpinned, and a rewrite that looked only at the first <c>xt</c>
    /// (the shape a <c>FirstOrDefault(p =&gt; p.StartsWith("xt="))</c> naturally produces) would have
    /// passed every other test in this file while silently dropping these releases.
    /// </summary>
    [Theory]
    [InlineData($"magnet:?xt=urn:sha1:0000000000000000000000000000000000000000&xt=urn:btih:{InfoHash}")]
    [InlineData($"magnet:?dn=Some.Release.1080p&xt=urn:btmh:1220abcd&xt=urn:btih:{InfoHash}")]
    [InlineData($"magnet:?xt=urn:btih:&xt=urn:btih:{InfoHash}")]
    public void A_btih_in_a_second_xt_parameter_is_admitted_and_its_hash_returned(string link)
    {
        Assert.True(TorznabFeedParser.TryValidateMagnetLink(link, out var validated, out var infoHash));
        Assert.Equal("magnet", validated.Scheme);
        Assert.Equal(InfoHash, infoHash);
    }

    /// <summary>
    /// sec-492: a RAW control character anywhere in the link is refused at admission. Without this,
    /// <c>DownloadProxyEndpoint</c>'s magnet arm hands <see cref="Uri.OriginalString"/> — which
    /// preserves a raw CR/LF verbatim, unlike <see cref="Uri.Query"/> — to <c>Results.Redirect</c>,
    /// and Kestrel throws on a control character in the <c>Location</c> header. So a malicious feed
    /// could turn any magnet item into a guaranteed 500 on download. The refusal closes it by
    /// construction one layer earlier, and this asserts the guard is ours rather than Kestrel's.
    ///
    /// <para>The last case is a NON-newline control character, so the rule pinned here is
    /// <c>char.IsControl</c> and not a CR/LF special case — an implementation narrowed to the two
    /// newline characters passes every other case and fails that one.</para>
    ///
    /// <para>Its positive control is the separate
    /// <see cref="A_magnet_whose_control_characters_are_percent_encoded_is_still_admitted"/>: the
    /// same CR/LF percent-ENCODED is still admitted, because that form stays encoded all the way
    /// into the header where it is inert text. Without it, a blanket "refuse anything mentioning
    /// 0D0A" would be over-broad and pass here unnoticed.</para>
    /// </summary>
    [Theory]
    [InlineData($"magnet:?xt=urn:btih:{InfoHash}&dn=Bad\r\nInjected: header")]
    [InlineData($"magnet:?xt=urn:btih:{InfoHash}&dn=Bad\nInjected: header")]
    [InlineData($"magnet:?xt=urn:btih:{InfoHash}&dn=Bad\rInjected: header")]
    [InlineData($"magnet:?xt=urn:btih:{InfoHash}\r\n&dn=Some.Release.1080p")]
    [InlineData($"magnet:?xt=urn:btih:{InfoHash}&dn=Bad\u0001Null")]
    public void A_magnet_carrying_a_raw_control_character_is_refused(string link)
    {
        Assert.False(TorznabFeedParser.TryValidateMagnetLink(link, out var validated, out var infoHash));
        Assert.Null(validated);
        Assert.Null(infoHash);
    }

    /// <inheritdoc cref="A_magnet_carrying_a_raw_control_character_is_refused"/>
    [Fact]
    public void A_magnet_whose_control_characters_are_percent_encoded_is_still_admitted()
    {
        // The encoded twin of the first refusal case above. It survives into the Location header as
        // the literal text "%0D%0A", which is not a control character and cannot split a response.
        var link = $"magnet:?xt=urn:btih:{InfoHash}&dn=Bad%0D%0AInjected:%20header";

        Assert.True(TorznabFeedParser.TryValidateMagnetLink(link, out var validated, out var infoHash));
        Assert.Equal(InfoHash, infoHash);
        Assert.DoesNotContain(validated.OriginalString, c => char.IsControl(c));
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
        Assert.False(TorznabFeedParser.TryValidateMagnetLink(link, out var validated, out var infoHash));
        Assert.Null(validated);
        Assert.Null(infoHash);
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

        Assert.False(TorznabFeedParser.TryValidateMagnetLink(Link, out _, out _));

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

    /// <summary>
    /// arb-4ysc: the whole point of parsing the btih — the parser carries it into
    /// <see cref="ReleaseCandidate.InfoHash"/>, which <c>IndexerXmlWriter</c> re-emits as
    /// <c>torznab:attr name="infohash"</c>. Until this, no production path set that field, so the
    /// writer's branch was dead and no *arr client ever saw a hash from a real feed.
    ///
    /// <para>Asserted PER ITEM over one feed carrying both kinds (CLAUDE.md §4): the magnet item
    /// carries its own hash AND the NZB item beside it carries none. An implementation that wrote
    /// one value to every row — the shape a misplaced assignment outside the loop produces — passes
    /// the first assertion and fails the second, which is why they are in one test rather than two.
    /// The second magnet pins that each item gets ITS OWN hash rather than the first one's.</para>
    /// </summary>
    [Fact]
    public void Each_magnet_item_carries_its_own_info_hash_while_an_nzb_item_beside_it_carries_none()
    {
        const string OtherHash = "0123456789abcdef0123456789abcdef01234567";

        var feed = BuildFeed(
            ("Magnet Release", Magnet(), null),
            ("Other Magnet Release", Magnet(OtherHash), null),
            ("Nzb Release", "https://indexer.example:9117/dl?id=1", null));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        Assert.Equal(InfoHash, Assert.Single(results, r => r.Title == "Magnet Release").InfoHash);
        Assert.Equal(OtherHash, Assert.Single(results, r => r.Title == "Other Magnet Release").InfoHash);

        // The NZB item has neither a magnet nor an infohash attr, so it must stay null — a hash on a
        // Usenet release is a claim the wire never made, and the writer would then advertise it.
        Assert.Null(Assert.Single(results, r => r.Title == "Nzb Release").InfoHash);
    }

    /// <summary>
    /// The btih is carried VERBATIM: neither case-folded nor converted between hex and base32. The
    /// value is rendered straight back out to the *arr client, so a hash we reshaped is no longer the
    /// one the indexer published — and base32 is upper-case by convention, so a defensive
    /// <c>ToLowerInvariant</c> would corrupt exactly the encoding it looks like it is normalising.
    /// </summary>
    [Theory]
    [InlineData("332afa1fd16fc0a5fd8d54e18d62e57f60a06764")]
    [InlineData("332AFA1FD16FC0A5FD8D54E18D62E57F60A06764")]
    [InlineData("GMVPUH6RN7AKL7MNKTQY2YXFP5QKA22E")]
    public void The_btih_reaches_the_candidate_exactly_as_the_feed_wrote_it(string hash)
    {
        var feed = BuildFeed(("Magnet Release", Magnet(hash), null));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        Assert.Equal(hash, Assert.Single(results).InfoHash);
    }

    /// <summary>
    /// Precedence when both sources are present and DISAGREE: the declared
    /// <c>torznab:attr name="infohash"</c> wins, the magnet's btih is the fallback. The attr is what
    /// the indexer chose to publish as the release's identity; the btih is inferred from a link that
    /// also carries trackers and a display name. Asserted with two DIFFERENT hashes, because equal
    /// ones would pass under either precedence and prove nothing.
    ///
    /// <para>The three items are one feed so the fallback direction is pinned beside the winner: an
    /// implementation that took the magnet unconditionally fails the first, one that took the attr
    /// unconditionally fails the second, and one that required both fails the third.</para>
    /// </summary>
    [Fact]
    public void A_declared_infohash_attr_wins_over_the_magnets_btih_which_remains_the_fallback()
    {
        const string DeclaredHash = "0123456789abcdef0123456789abcdef01234567";

        var feed = BuildFeedWithInfoHashAttr(
            ("Both Disagree", Magnet(), DeclaredHash),
            ("Magnet Only", Magnet(), null),
            ("Attr Only", "https://indexer.example:9117/dl?id=1", DeclaredHash));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        Assert.Equal(DeclaredHash, Assert.Single(results, r => r.Title == "Both Disagree").InfoHash);
        Assert.Equal(InfoHash, Assert.Single(results, r => r.Title == "Magnet Only").InfoHash);
        Assert.Equal(DeclaredHash, Assert.Single(results, r => r.Title == "Attr Only").InfoHash);
    }

    /// <summary>
    /// A blank <c>infohash</c> attr does not beat a real btih. An indexer that emits the attribute
    /// unconditionally and leaves it empty would otherwise blank out a hash the magnet did supply —
    /// "the attr wins" is about what the indexer SAID, and an empty attr said nothing. Refused by
    /// <c>TorznabFeedParser</c>'s shape gate (<c>IsAdmissibleDeclaredInfoHash</c>, arb-xgv3): neither
    /// an empty string nor a whitespace run fits any of the three admitted shapes (40 hex, 32 base32,
    /// 64 hex).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_infohash_attr_falls_back_to_the_magnets_btih(string declared)
    {
        var feed = BuildFeedWithInfoHashAttr(("Magnet Release", Magnet(), declared));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        Assert.Equal(InfoHash, Assert.Single(results).InfoHash);
    }

    private static string BuildFeed(params (string Title, string Link, string? Protocol)[] items) =>
        BuildFeed(items.Select(item => (item.Title, item.Link, item.Protocol, (string?)null)).ToArray());

    private static string BuildFeedWithInfoHashAttr(params (string Title, string Link, string? InfoHashAttr)[] items) =>
        BuildFeed(items.Select(item => (item.Title, item.Link, (string?)null, item.InfoHashAttr)).ToArray());

    private static string BuildFeed((string Title, string Link, string? Protocol, string? InfoHashAttr)[] items)
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
                            new XAttribute("value", item.Protocol)),
                    item.InfoHashAttr is null
                        ? null
                        : new XElement(torznab + "attr",
                            new XAttribute("name", "infohash"),
                            new XAttribute("value", item.InfoHashAttr))))));

        return new XDocument(rss).ToString();
    }
}
