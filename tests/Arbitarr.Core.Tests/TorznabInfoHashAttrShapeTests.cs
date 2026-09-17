using System.Xml.Linq;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-xgv3: the declared <c>torznab:attr name="infohash"</c> is admitted only by shape (40 hex, 32
/// base32, or 64 hex) and is never transformed — an admitted value comes back byte-for-byte, and
/// anything else falls back to the magnet's btih exactly as an absent attr would.
///
/// <para>Follow-up from #507's security + code review: the <c>char.IsControl</c> refusal added
/// there covers only the magnet link, so a declared attr of e.g. a raw CR/LF was carried verbatim
/// into <see cref="ReleaseCandidate.InfoHash"/> unbounded in length and charset. Closing admission to
/// the three real shapes closes that as a side effect, since a control character or an oversized
/// value fails every shape.</para>
/// </summary>
public sealed class TorznabInfoHashAttrShapeTests
{
    private const string MagnetHash = "332afa1fd16fc0a5fd8d54e18d62e57f60a06764";

    private static readonly Uri Origin = new("http://indexer.example:9117/");

    /// <summary>
    /// Built via the <see cref="XElement"/>/<see cref="XAttribute"/> API and serialized, rather than
    /// interpolated into a raw XML string, so a value containing an XML-legal control character
    /// (e.g. a bare CR, which is <c>#xD</c> in the XML 1.0 <c>Char</c> production) round-trips
    /// correctly instead of needing hand-written escaping.
    /// </summary>
    private static string FeedWithDeclaredAttr(string? declaredValue)
    {
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";

        var doc = new XDocument(new XElement("rss",
            new XAttribute(XNamespace.Xmlns + "torznab", torznab),
            new XElement("channel",
                new XElement("item",
                    new XElement("title", "Example Release 1080p"),
                    new XElement("guid", "guid-1"),
                    new XElement("link", $"magnet:?xt=urn:btih:{MagnetHash}&dn=Some.Release.1080p"),
                    new XElement("pubDate", "Thu, 27 Aug 2026 12:00:00 +0000"),
                    declaredValue is null
                        ? null
                        : new XElement(torznab + "attr",
                            new XAttribute("name", "infohash"),
                            new XAttribute("value", declaredValue))))));

        return doc.ToString();
    }

    private static string? ParsedInfoHash(string declaredValue)
    {
        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(FeedWithDeclaredAttr(declaredValue), Origin));
        return item.InfoHash;
    }

    /// <summary>
    /// Same shape as <see cref="FeedWithDeclaredAttr"/>, but with an arbitrary number of
    /// <c>infohash</c> attrs on the one item, in the order given — for pinning that only the FIRST
    /// declared attr in document order is ever considered.
    /// </summary>
    private static string FeedWithDeclaredAttrs(params string[] declaredValues)
    {
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";

        var doc = new XDocument(new XElement("rss",
            new XAttribute(XNamespace.Xmlns + "torznab", torznab),
            new XElement("channel",
                new XElement("item",
                    new XElement("title", "Example Release 1080p"),
                    new XElement("guid", "guid-1"),
                    new XElement("link", $"magnet:?xt=urn:btih:{MagnetHash}&dn=Some.Release.1080p"),
                    new XElement("pubDate", "Thu, 27 Aug 2026 12:00:00 +0000"),
                    declaredValues.Select(v => new XElement(torznab + "attr",
                        new XAttribute("name", "infohash"),
                        new XAttribute("value", v)))))));

        return doc.ToString();
    }

    private static string? ParsedInfoHashFor(params string[] declaredValues)
    {
        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(FeedWithDeclaredAttrs(declaredValues), Origin));
        return item.InfoHash;
    }

    // --- Admitted shapes: carried verbatim -----------------------------------------------------

    [Fact]
    public void A_40_char_hex_attr_is_admitted_verbatim()
    {
        const string declared = "0123456789abcdef0123456789abcdef01234567";

        Assert.Equal(declared, ParsedInfoHash(declared));
    }

    [Fact]
    public void A_32_char_base32_attr_is_admitted_verbatim_in_upper_case()
    {
        const string declared = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        Assert.Equal(declared, ParsedInfoHash(declared));
    }

    [Fact]
    public void A_32_char_base32_attr_is_admitted_verbatim_in_lower_case()
    {
        // Same alphabet, lower case: base32 is upper-case by CONVENTION, but this admission does not
        // reshape case, so a lower-case value must survive exactly as written rather than being
        // upper-cased (which would corrupt a value that was never actually upper-case base32).
        const string declared = "abcdefghijklmnopqrstuvwxyz234567";

        Assert.Equal(declared, ParsedInfoHash(declared));
    }

    [Fact]
    public void A_64_char_hex_attr_v2_btmh_is_admitted_verbatim()
    {
        const string declared = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd12";

        Assert.Equal(64, declared.Length);
        Assert.Equal(declared, ParsedInfoHash(declared));
    }

    // --- Refused shapes: fall back to the magnet's btih -----------------------------------------
    //
    // Each refusal test FIRST demonstrates the positive control (a well-formed attr IS carried) in
    // the very same fixture shape, then shows the malformed one falls back to the magnet's btih —
    // asserting the btih VALUE, not merely "not the bad value" (CLAUDE.md §4).

    [Fact]
    public void A_well_formed_attr_is_the_positive_control_for_every_refusal_below()
    {
        const string declared = "0123456789abcdef0123456789abcdef01234567";

        Assert.Equal(declared, ParsedInfoHash(declared));
    }

    [Fact]
    public void An_attr_with_an_embedded_cr_is_refused_and_falls_back_to_the_magnet_btih()
    {
        var declared = "0123456789abcdef0123456789abcdef0123456" + "\r";

        Assert.Equal(MagnetHash, ParsedInfoHash(declared));
    }

    /// <summary>
    /// A raw NUL cannot reach the shape check at all: XML 1.0's <c>Char</c> production admits only
    /// tab/LF/CR among the C0 control characters, so a NUL anywhere in the document — not just in
    /// this attr — makes the WHOLE document fail to parse, before any item (let alone this attr) is
    /// read. <see cref="XAttribute"/>'s own constructor enforces the same rule when the document is
    /// built via the object model rather than text, which is what this fixture uses throughout. The
    /// refusal is therefore observed one layer up, as a parse failure, rather than as a fallback to
    /// the magnet's btih — there is no well-formed feed for the fallback path to ever see a raw NUL
    /// in.
    /// </summary>
    [Fact]
    public void An_attr_with_an_embedded_nul_cannot_form_a_well_formed_feed_and_is_refused_by_the_xml_layer_itself()
    {
        Assert.Throws<ArgumentException>(() =>
            FeedWithDeclaredAttr("0123456789abcdef0123456789abcdef0123456" + "\0"));
    }

    [Fact]
    public void A_39_char_hex_looking_attr_is_refused_and_falls_back_to_the_magnet_btih()
    {
        const string declared = "0123456789abcdef0123456789abcdef0123456";

        Assert.Equal(39, declared.Length);
        Assert.Equal(MagnetHash, ParsedInfoHash(declared));
    }

    [Fact]
    public void A_41_char_hex_looking_attr_is_refused_and_falls_back_to_the_magnet_btih()
    {
        const string declared = "0123456789abcdef0123456789abcdef012345678";

        Assert.Equal(41, declared.Length);
        Assert.Equal(MagnetHash, ParsedInfoHash(declared));
    }

    [Fact]
    public void A_1_megabyte_attr_is_refused_and_falls_back_to_the_magnet_btih()
    {
        var declared = new string('a', 1024 * 1024);

        Assert.Equal(MagnetHash, ParsedInfoHash(declared));
    }

    /// <summary>
    /// Non-ASCII digits: pins that the shape check is ordinal/ASCII-only, not
    /// <see cref="char.IsLetterOrDigit(char)"/> or any other culture-sensitive class that would admit
    /// a Unicode digit merely because it "looks like" a digit and the length matches.
    /// </summary>
    [Fact]
    public void An_attr_of_non_ascii_digits_is_refused_and_falls_back_to_the_magnet_btih()
    {
        // U+FF10..U+FF19 are the fullwidth digit forms — 40 code points, so length alone would pass.
        var declared = string.Concat(Enumerable.Repeat("０１２３", 10));

        Assert.Equal(40, declared.Length);
        Assert.Equal(MagnetHash, ParsedInfoHash(declared));
    }

    [Fact]
    public void An_attr_with_surrounding_whitespace_is_refused_and_falls_back_to_the_magnet_btih()
    {
        var declared = "  0123456789abcdef0123456789abcdef01234567  ";

        Assert.Equal(MagnetHash, ParsedInfoHash(declared));
    }

    // --- Multiple infohash attrs on one item: only the FIRST in document order is considered ----

    /// <summary>
    /// Two well-formed attrs with DIFFERENT values: the first wins, the second is never consulted.
    /// Using different values (rather than two copies of the same hash) is the point — equal values
    /// would pass whichever one an implementation picked, proving nothing about ordering.
    /// </summary>
    [Fact]
    public void Two_well_formed_attrs_with_different_values_resolve_to_the_first_in_document_order()
    {
        const string first = "0123456789abcdef0123456789abcdef01234567";
        const string second = "fedcba9876543210fedcba9876543210fedcba9";

        Assert.Equal(first, ParsedInfoHashFor(first, second));
    }

    /// <summary>
    /// A malformed first attr followed by a well-formed second: the fallback to the magnet's btih
    /// applies exactly as it does for a single malformed attr — the well-formed SECOND attr is not
    /// scanned for. Positive control first (a lone well-formed attr resolves to itself), so this
    /// reads as "the second attr was skipped", not "attrs are never read at all".
    /// </summary>
    [Fact]
    public void A_malformed_first_attr_falls_back_to_the_magnet_btih_even_when_a_second_attr_is_well_formed()
    {
        const string wellFormed = "0123456789abcdef0123456789abcdef01234567";

        Assert.Equal(wellFormed, ParsedInfoHashFor(wellFormed));

        const string malformedFirst = "not-a-valid-shape";
        Assert.Equal(MagnetHash, ParsedInfoHashFor(malformedFirst, wellFormed));
    }
}
