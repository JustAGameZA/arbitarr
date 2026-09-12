using System.Xml.Linq;
using Arbitarr.Core.Sources;
using Arbitarr.TestSupport;

namespace Arbitarr.Core.Tests;

/// <summary>
/// SEC-M1's link pin (arb-07ei), driven by the shared <see cref="UpstreamOriginCorpus"/> — the same
/// corpus <c>SourceRepositoryTests</c> drives at the write boundary, so a form added later is tested
/// against both and the two boundaries cannot drift apart again.
///
/// <para>The corpus theory is the breadth; the named facts below are the properties the beads name,
/// asserted individually so a failure reports which one broke rather than only which string did.</para>
/// </summary>
public sealed class OriginPinnedLinkTests
{
    [Theory]
    [MemberData(nameof(UpstreamOriginCorpus.PinCases), MemberType = typeof(UpstreamOriginCorpus))]
    public void The_pin_accepts_exactly_the_corpus_links_at_their_own_origin(
        string link,
        string origin,
        bool accepted,
        string because)
    {
        var allowedOrigin = new Uri(origin, UriKind.Absolute);

        var result = TorznabFeedParser.TryValidateOriginPinnedLink(link, allowedOrigin, out var validated);

        Assert.True(result == accepted, $"link '{link}' against origin '{origin}' ({because})");

        // The out parameter is part of the contract: a refused link must not hand the caller a Uri
        // it could fetch anyway.
        if (accepted)
        {
            Assert.NotNull(validated);
        }
        else
        {
            Assert.Null(validated);
        }
    }

    /// <summary>
    /// S4, per direction. A downgrade test passing proves nothing about an upgrade: an
    /// implementation comparing <c>link.Scheme == "https"</c> would pass one and fail the other.
    /// </summary>
    [Fact]
    public void A_scheme_downgrade_is_refused()
    {
        var refused = TorznabFeedParser.TryValidateOriginPinnedLink(
            "http://indexer.example:9117/dl",
            new Uri("https://indexer.example:9117"),
            out _);

        Assert.False(refused);
    }

    /// <summary>S4, the mirrored direction.</summary>
    [Fact]
    public void A_scheme_upgrade_is_also_refused()
    {
        var refused = TorznabFeedParser.TryValidateOriginPinnedLink(
            "https://indexer.example:9117/dl",
            new Uri("http://indexer.example:9117"),
            out _);

        Assert.False(refused);
    }

    /// <summary>
    /// S3 at the unit level. <see cref="Uri.Host"/> excludes userinfo, which is why comparing host
    /// and port alone accepted a link that injects a Basic-auth credential into a URL Arbitarr then
    /// fetches.
    /// </summary>
    [Fact]
    public void A_link_carrying_userinfo_is_refused()
    {
        var refused = TorznabFeedParser.TryValidateOriginPinnedLink(
            $"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117/dl",
            new Uri("https://indexer.example:9117"),
            out _);

        Assert.False(refused);
    }

    /// <summary>
    /// S5 — the positive control for every refusal above. Without it a pin that returned
    /// <c>false</c> unconditionally would satisfy S3 and S4 while breaking the product entirely.
    /// </summary>
    [Fact]
    public void A_matching_link_is_still_accepted()
    {
        var accepted = TorznabFeedParser.TryValidateOriginPinnedLink(
            "https://indexer.example:9117/dl?id=1",
            new Uri("https://indexer.example:9117"),
            out var validated);

        Assert.True(accepted);
        Assert.Equal("https://indexer.example:9117/dl?id=1", validated.AbsoluteUri);
    }

    /// <summary>
    /// S3/S5 at the PARSER level, per item. The unit assertions above prove what the pin returns;
    /// they do not prove <see cref="TorznabFeedParser.ParseFeedResponse"/> consults it. This drives
    /// a three-item feed and asserts PER ITEM which survived — "some item was dropped" would still
    /// pass against an implementation that dropped all three, including the legitimate one.
    /// </summary>
    [Fact]
    public void ParseFeedResponse_drops_the_userinfo_and_downgraded_items_and_keeps_the_matching_one()
    {
        var feed = BuildFeed(
            ("Legitimate", "https://indexer.example:9117/dl?id=1"),
            ("Credential Injection", $"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117/dl?id=2"),
            ("Cleartext Downgrade", "http://indexer.example:9117/dl?id=3"));

        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));

        var titles = results.Select(r => r.Title).ToArray();
        Assert.Contains("Legitimate", titles);
        Assert.DoesNotContain("Credential Injection", titles);
        Assert.DoesNotContain("Cleartext Downgrade", titles);
        Assert.Single(results);

        // Non-vacuity: the planted password must not have survived into ANY projected link either,
        // and the assertion is shown to bite below.
        Assert.DoesNotContain(
            UpstreamOriginCorpus.PlantedPassword,
            string.Join('\n', results.Select(r => r.Link.AbsoluteUri)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The positive control for the absence assertion above: the same search, run against an origin
    /// that MATCHES the credential-bearing link's host and port, would find the planted password in
    /// a projected link if the pin let it through. It does not — so instead this demonstrates the
    /// assertion is capable of firing, by running it against the raw feed text, which does carry the
    /// password. An absence assertion whose haystack never contained the needle proves nothing
    /// (CLAUDE.md §4).
    /// </summary>
    [Fact]
    public void The_planted_password_assertion_is_capable_of_firing()
    {
        var feed = BuildFeed(("Credential Injection", $"https://user:{UpstreamOriginCorpus.PlantedPassword}@indexer.example:9117/dl?id=2"));

        // The needle IS in the haystack the parser was given...
        Assert.Contains(UpstreamOriginCorpus.PlantedPassword, feed, StringComparison.Ordinal);

        // ...and is absent from what the parser produced, because the item was dropped.
        var results = TorznabFeedParser.ParseFeedResponse(feed, new Uri("https://indexer.example:9117"));
        Assert.Empty(results);
    }

    private static string BuildFeed(params (string Title, string Link)[] items)
    {
        var rss = new XElement("rss",
            new XElement("channel",
                items.Select(item => new XElement("item",
                    new XElement("title", item.Title),
                    new XElement("guid", item.Title),
                    new XElement("link", item.Link),
                    new XElement("size", "1024")))));

        return new XDocument(rss).ToString();
    }
}
