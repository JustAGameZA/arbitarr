using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Sources.Newznab.Tests;

/// <summary>
/// Parse-level tests for <see cref="TorznabFeedParser"/> — the parser both adapters now share.
/// Both families are exercised because the point of the shared parser is that a
/// <c>newznab:attr</c> and a <c>torznab:attr</c> reach the same code: they differ only in prefix,
/// and the parser matches the schema NAMESPACE. A test for one family alone would leave that claim
/// unproven.
/// </summary>
public class NewznabFeedParsingTests
{
    private static readonly Uri Origin = new("http://indexer.example:9117/");

    [Fact]
    public void ParseFeedResponse_TorznabItem_ReadsSizeAndCategoryFromTorznabAttrs()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
              <item>
                <title>Example Release 1080p</title>
                <guid>torznab-guid-1</guid>
                <link>http://indexer.example:9117/download/1</link>
                <pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>
                <torznab:attr name="size" value="7340032" />
                <torznab:attr name="category" value="5040" />
                <torznab:attr name="protocol" value="torrent" />
              </item>
            </channel></rss>
            """;

        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(xml, Origin));

        Assert.Equal(7340032, item.Size);
    }

    [Fact]
    public void ParseFeedResponse_TorznabItem_ReadsTheCategoryAttr()
    {
        var xml = TorznabItemFeed(category: "5040");

        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(xml, Origin));

        Assert.Equal(new[] { 5040 }, item.Category);
    }

    /// <summary>
    /// The same document with the NEWZNAB prefix. It must parse identically, because the prefix is
    /// not what the parser matches on — the schema namespace URI is, and both families use it.
    /// </summary>
    [Fact]
    public void ParseFeedResponse_NewznabItem_ReadsSizeFromANewznabPrefixedAttr()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:newznab="http://torznab.com/schemas/2015/feed"><channel>
              <item>
                <title>Example Usenet Release</title>
                <guid>newznab-guid-1</guid>
                <link>http://indexer.example:9117/getnzb/1</link>
                <pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>
                <newznab:attr name="size" value="1048576" />
                <newznab:attr name="category" value="5000" />
                <newznab:attr name="protocol" value="usenet" />
              </item>
            </channel></rss>
            """;

        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(xml, Origin));

        Assert.Equal(1048576, item.Size);
    }

    [Fact]
    public void ParseFeedResponse_NewznabItem_ReadsTheProtocolAttrAsUsenet()
    {
        var xml = NewznabItemFeed(protocolAttr: "usenet", enclosureType: null);

        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(xml, Origin));

        Assert.Equal(ProtocolKind.Usenet, item.Protocol);
    }

    /// <summary>
    /// The enclosure-type FALLBACK: an indexer that emits no protocol attr is classified from the
    /// enclosure's MIME type instead. Torrent is the case that has to be detected, because the
    /// no-signal default is Usenet — a fallback that silently returned Usenet for a torrent feed
    /// would be invisible in a result list.
    /// </summary>
    [Fact]
    public void ParseFeedResponse_WithNoProtocolAttr_FallsBackToATorrentEnclosureType()
    {
        var xml = NewznabItemFeed(protocolAttr: null, enclosureType: "application/x-bittorrent");

        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(xml, Origin));

        Assert.Equal(ProtocolKind.Torrent, item.Protocol);
    }

    [Fact]
    public void ParseFeedResponse_WithNoProtocolAttrAndANonTorrentEnclosure_ClassifiesAsUsenet()
    {
        var xml = NewznabItemFeed(protocolAttr: null, enclosureType: "application/x-nzb");

        var item = Assert.Single(TorznabFeedParser.ParseFeedResponse(xml, Origin));

        Assert.Equal(ProtocolKind.Usenet, item.Protocol);
    }

    // ---------------------------------------------------------------------
    // SEC-M1 origin pinning: a non-conforming link drops THAT item, and only that item.
    //
    // Asserted BY ID, per item, never as "fewer items came back". A count assertion passes for the
    // wrong reason if the parser drops a different item than the off-origin one, or drops two — and
    // it would still pass if the parser replaced the bad link with a placeholder URI, which is the
    // exact failure the drop exists to prevent.
    // ---------------------------------------------------------------------

    private const string OffOriginThreeItemFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
          <item>
            <title>First</title>
            <guid>item-1</guid>
            <link>http://indexer.example:9117/download/1</link>
            <torznab:attr name="size" value="1" />
          </item>
          <item>
            <title>Second</title>
            <guid>item-2</guid>
            <link>http://attacker.example:9117/download/2</link>
            <torznab:attr name="size" value="2" />
          </item>
          <item>
            <title>Third</title>
            <guid>item-3</guid>
            <link>http://indexer.example:9117/download/3</link>
            <torznab:attr name="size" value="3" />
          </item>
        </channel></rss>
        """;

    [Fact]
    public void ParseFeedResponse_OffOriginLink_KeepsTheFirstOnOriginItem()
    {
        var results = TorznabFeedParser.ParseFeedResponse(OffOriginThreeItemFeed, Origin);

        Assert.Contains(results, candidate => candidate.Guid == "item-1");
    }

    [Fact]
    public void ParseFeedResponse_OffOriginLink_DropsExactlyThatItemById()
    {
        var results = TorznabFeedParser.ParseFeedResponse(OffOriginThreeItemFeed, Origin);

        Assert.DoesNotContain(results, candidate => candidate.Guid == "item-2");
    }

    [Fact]
    public void ParseFeedResponse_OffOriginLink_KeepsTheOnOriginItemAfterIt()
    {
        var results = TorznabFeedParser.ParseFeedResponse(OffOriginThreeItemFeed, Origin);

        Assert.Contains(results, candidate => candidate.Guid == "item-3");
    }

    /// <summary>
    /// Non-vacuity control for the three assertions above: the dropped item IS present in the
    /// fixture and IS parseable — it comes back when the parser is pointed at the origin its link
    /// names. Without this, "item-2 is absent" would pass equally well if the fixture were
    /// malformed and no item parsed at all.
    /// </summary>
    [Fact]
    public void ParseFeedResponse_TheDroppedItemIsParseable_WhenItsOwnOriginIsTheAllowedOne()
    {
        var results = TorznabFeedParser.ParseFeedResponse(
            OffOriginThreeItemFeed,
            new Uri("http://attacker.example:9117/"));

        Assert.Contains(results, candidate => candidate.Guid == "item-2");
    }

    [Fact]
    public void ParseFeedResponse_AnOffOriginLinkIsNeverReplacedByAPlaceholderUri()
    {
        var results = TorznabFeedParser.ParseFeedResponse(OffOriginThreeItemFeed, Origin);

        Assert.DoesNotContain(results, candidate => candidate.Link!.Host != Origin.Host);
    }

    /// <summary>A same-host link on a DIFFERENT port is off-origin: the pin is scheme+host+port.</summary>
    [Fact]
    public void ParseFeedResponse_SameHostDifferentPort_IsDropped()
    {
        var xml = TorznabItemFeed(category: "5040", link: "http://indexer.example:8080/download/1");

        Assert.Empty(TorznabFeedParser.ParseFeedResponse(xml, Origin));
    }

    [Fact]
    public void ParseFeedResponse_ANonHttpSchemeLink_IsDropped()
    {
        var xml = TorznabItemFeed(category: "5040", link: "file:///etc/passwd");

        Assert.Empty(TorznabFeedParser.ParseFeedResponse(xml, Origin));
    }

    // ---------------------------------------------------------------------
    // Caps
    // ---------------------------------------------------------------------

    private const string NewznabCaps = """
        <?xml version="1.0" encoding="UTF-8"?>
        <caps>
          <limits max="100" default="50" />
          <searching>
            <search available="yes" supportedParams="q" />
            <tv-search available="yes" supportedParams="q,season,ep,tvdbid" />
            <movie-search available="no" supportedParams="q" />
          </searching>
          <categories>
            <category id="5000" name="TV">
              <subcat id="5040" name="TV/HD" />
            </category>
          </categories>
        </caps>
        """;

    private const string TorznabCaps = """
        <?xml version="1.0" encoding="UTF-8"?>
        <caps>
          <limits max="200" default="100" />
          <searching>
            <search available="yes" supportedParams="q" />
            <tv-search available="no" supportedParams="q" />
            <movie-search available="yes" supportedParams="q,imdbid" />
          </searching>
          <categories>
            <category id="2000" name="Movies" />
          </categories>
        </caps>
        """;

    [Fact]
    public void ParseCapsResponse_Newznab_ReadsTheMaxPageSizeLimit()
    {
        Assert.Equal(100, TorznabFeedParser.ParseCapsResponse(NewznabCaps).MaxPageSize);
    }

    [Fact]
    public void ParseCapsResponse_Newznab_ReadsTheCategoryAndItsSubcategory()
    {
        var caps = TorznabFeedParser.ParseCapsResponse(NewznabCaps);

        Assert.Equal(new[] { 5000, 5040 }, caps.SupportedCategories);
    }

    [Fact]
    public void ParseCapsResponse_Newznab_ReportsTvSearchAvailable()
    {
        Assert.True(TorznabFeedParser.ParseCapsResponse(NewznabCaps).SupportsTvSearch);
    }

    [Fact]
    public void ParseCapsResponse_Newznab_ReportsMovieSearchUnavailable()
    {
        Assert.False(TorznabFeedParser.ParseCapsResponse(NewznabCaps).SupportsMovieSearch);
    }

    [Fact]
    public void ParseCapsResponse_Newznab_UnionsTheSupportedParamsAcrossSearchModes()
    {
        var caps = TorznabFeedParser.ParseCapsResponse(NewznabCaps);

        Assert.Equal(new[] { "ep", "q", "season", "tvdbid" }, caps.SupportedParams);
    }

    [Fact]
    public void ParseCapsResponse_Newznab_PreservesTheSubcategoryDisplayName()
    {
        var caps = TorznabFeedParser.ParseCapsResponse(NewznabCaps);

        Assert.Equal("TV/HD", caps.CategoryNames![5040]);
    }

    [Fact]
    public void ParseCapsResponse_Torznab_ReadsTheMaxPageSizeLimit()
    {
        Assert.Equal(200, TorznabFeedParser.ParseCapsResponse(TorznabCaps).MaxPageSize);
    }

    [Fact]
    public void ParseCapsResponse_Torznab_ReadsTheCategory()
    {
        Assert.Equal(new[] { 2000 }, TorznabFeedParser.ParseCapsResponse(TorznabCaps).SupportedCategories);
    }

    [Fact]
    public void ParseCapsResponse_Torznab_ReportsMovieSearchAvailable()
    {
        Assert.True(TorznabFeedParser.ParseCapsResponse(TorznabCaps).SupportsMovieSearch);
    }

    [Fact]
    public void ParseCapsResponse_Torznab_ReportsTvSearchUnavailable()
    {
        Assert.False(TorznabFeedParser.ParseCapsResponse(TorznabCaps).SupportsTvSearch);
    }

    [Fact]
    public void ParseCapsResponse_WithNoLimitsElement_LeavesMaxPageSizeUnset()
    {
        var caps = TorznabFeedParser.ParseCapsResponse(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><caps><categories /></caps>");

        Assert.Null(caps.MaxPageSize);
    }

    private static string TorznabItemFeed(string category, string link = "http://indexer.example:9117/download/1") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
          <item>
            <title>Example Release</title>
            <guid>torznab-guid-1</guid>
            <link>{link}</link>
            <torznab:attr name="size" value="7340032" />
            <torznab:attr name="category" value="{category}" />
          </item>
        </channel></rss>
        """;

    private static string NewznabItemFeed(string? protocolAttr, string? enclosureType)
    {
        var protocolLine = protocolAttr is null
            ? string.Empty
            : $"<newznab:attr name=\"protocol\" value=\"{protocolAttr}\" />";
        var enclosureLine = enclosureType is null
            ? string.Empty
            : $"<enclosure url=\"http://indexer.example:9117/download/1\" type=\"{enclosureType}\" />";

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:newznab="http://torznab.com/schemas/2015/feed"><channel>
              <item>
                <title>Example Release</title>
                <guid>newznab-guid-1</guid>
                <link>http://indexer.example:9117/download/1</link>
                <newznab:attr name="size" value="1048576" />
                {protocolLine}
                {enclosureLine}
              </item>
            </channel></rss>
            """;
    }
}
