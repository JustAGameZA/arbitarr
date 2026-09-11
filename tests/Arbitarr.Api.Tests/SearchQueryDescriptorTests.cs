using Arbitarr.Api.Search;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-2b6 (audit F-010): the SearchServed event descriptor must identify WHICH search it is about.
/// Before this, the reason was built from the query text alone, so every RSS and id-based search —
/// which carry no text — rendered as <c>Query '' (tvsearch)</c> and 55 events were mutually
/// indistinguishable.
/// </summary>
public class SearchQueryDescriptorTests
{
    private static SearchQuery Query(
        string? text = null,
        IReadOnlyList<int>? categories = null,
        int? tvdbId = null,
        int? tmdbId = null,
        int? season = null,
        int? episode = null,
        SearchType type = SearchType.Search,
        int? absolute = null) =>
        new(
            text,
            categories ?? Array.Empty<int>(),
            Limit: 100,
            Protocol: SearchProtocol.Torznab,
            Offset: 0,
            TvdbId: tvdbId,
            TmdbId: tmdbId,
            Season: season,
            Episode: episode,
            Type: type,
            Absolute: absolute);

    [Fact]
    public void TextOnlyQueryRendersTheTextAndTheMode()
    {
        var query = Query(text: "bleach");

        Assert.Equal("Query 'bleach' (search)", SearchQueryDescriptor.Describe(query));
        Assert.Equal("type=search;q=bleach", SearchQueryDescriptor.DescribeDetail(query));
    }

    [Fact]
    public void CategoriesOnlyQueryIsNamedAsAFeedRatherThanEmptyQuotes()
    {
        // The RSS-sync shape, and the single largest contributor to F-010: no text, no ids, just
        // categories. `Query ''` is what made these indistinguishable from each other.
        var query = Query(categories: new[] { 5030, 5040 }, type: SearchType.TvSearch);

        var reason = SearchQueryDescriptor.Describe(query);

        Assert.Equal("Query (rss feed) (tvsearch) cats=5030,5040", reason);
        Assert.DoesNotContain("''", reason, StringComparison.Ordinal);
        Assert.Equal("type=tvsearch;cats=5030,5040", SearchQueryDescriptor.DescribeDetail(query));
    }

    [Fact]
    public void IdSeasonAndEpisodeAreAllRendered()
    {
        var query = Query(
            categories: new[] { 5030, 5040 },
            tvdbId: 74796,
            season: 2,
            episode: 5,
            type: SearchType.TvSearch);

        Assert.Equal(
            "Query (tvsearch) cats=5030,5040 tvdbid=74796 S02E05",
            SearchQueryDescriptor.Describe(query));
        Assert.Equal(
            "type=tvsearch;cats=5030,5040;tvdbid=74796;season=2;episode=5",
            SearchQueryDescriptor.DescribeDetail(query));
    }

    [Fact]
    public void MovieQueryRendersTheTmdbId()
    {
        var query = Query(text: "dune", tmdbId: 438631, type: SearchType.Movie);

        Assert.Equal("Query 'dune' (movie) tmdbid=438631", SearchQueryDescriptor.Describe(query));
        Assert.Equal("type=movie;tmdbid=438631;q=dune", SearchQueryDescriptor.DescribeDetail(query));
    }

    [Fact]
    public void AbsoluteZeroIsRenderedBecauseZeroIsARealValue()
    {
        // arb-u1c: SearchCacheKeyBuilder distinguishes abs=0 from abs=none and gives each its own
        // cache row. A truthiness check would omit exactly the request whose row is its own, so the
        // descriptor would stop identifying the one search this field exists to separate.
        var query = Query(text: "0", tvdbId: 74796, type: SearchType.TvSearch, absolute: 0);

        Assert.Contains("abs=0", SearchQueryDescriptor.Describe(query), StringComparison.Ordinal);
        Assert.Contains("abs=0", SearchQueryDescriptor.DescribeDetail(query), StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentAbsoluteIsOmittedEntirely()
    {
        // The companion to the test above: proving "abs=0" renders is only meaningful if an ABSENT
        // absolute renders nothing, otherwise both cases would print something and the field would
        // not distinguish them.
        var query = Query(text: "bleach", tvdbId: 74796, type: SearchType.TvSearch);

        Assert.DoesNotContain("abs=", SearchQueryDescriptor.Describe(query), StringComparison.Ordinal);
        Assert.DoesNotContain("abs=", SearchQueryDescriptor.DescribeDetail(query), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyQueryWithNoIdsRendersTheFeedSpellingRatherThanEmptyQuotes()
    {
        var query = Query(text: "   ");

        Assert.Equal("Query (rss feed) (search)", SearchQueryDescriptor.Describe(query));
        Assert.Equal("type=search", SearchQueryDescriptor.DescribeDetail(query));
    }

    [Fact]
    public void TwoSearchesDifferingOnlyByIdProduceDifferentDescriptors()
    {
        // The bead's actual complaint, at the descriptor level: these two were byte-identical.
        var first = Query(tvdbId: 74796, type: SearchType.TvSearch);
        var second = Query(tvdbId: 81797, type: SearchType.TvSearch);

        Assert.NotEqual(SearchQueryDescriptor.Describe(first), SearchQueryDescriptor.Describe(second));
        Assert.NotEqual(
            SearchQueryDescriptor.DescribeDetail(first),
            SearchQueryDescriptor.DescribeDetail(second));
    }

    /// <summary>
    /// CLAUDE.md §4: the absence half of this assertion only bites because the presence half proves
    /// the planted values reached the descriptor at all. Asserting the apikey is absent from a
    /// descriptor built from an empty query would pass just as happily with the feature removed.
    /// </summary>
    [Fact]
    public void ApiKeyOnTheRequestCannotReachTheReasonOrTheDetail()
    {
        // A realistic inbound query string: the client's apikey travels on it, and /api/activity is
        // un-gated (CLAUDE.md §1). The SearchQuery is then built from the PARSED parameters exactly
        // as SearchEndpoint builds it — which is the mechanism under test: the record has nowhere to
        // put an apikey, so the descriptor structurally cannot render one.
        const string PlantedKey = "PLANTEDAPIKEY";
        const string PlantedText = "PLANTEDTEXT";

        var parsed = QueryHelpers.ParseQuery(
            $"?t=tvsearch&apikey={PlantedKey}&q={PlantedText}&tvdbid=74796&cat=5030");

        var query = new SearchQuery(
            parsed["q"].ToString(),
            parsed["cat"].ToString().Split(',').Select(int.Parse).ToArray(),
            Limit: 100,
            Protocol: SearchProtocol.Torznab,
            Offset: 0,
            TvdbId: int.Parse(parsed["tvdbid"].ToString()),
            Type: SearchTypeParser.Parse(parsed["t"].ToString()));

        var reason = SearchQueryDescriptor.Describe(query);
        var detail = SearchQueryDescriptor.DescribeDetail(query);

        // POSITIVE CONTROL: the planted text DID travel this path, so these strings are the ones a
        // leak would have appeared in.
        Assert.Contains(PlantedText, reason, StringComparison.Ordinal);
        Assert.Contains(PlantedText, detail, StringComparison.Ordinal);

        // And the planted key did not.
        Assert.DoesNotContain(PlantedKey, reason, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedKey, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModeComesFromTheParsedTypeRatherThanAnArbitraryWireValue()
    {
        // The reason used to interpolate the raw `t=` string, so a caller's arbitrary value was
        // echoed verbatim into an un-gated feed. SearchTypeParser closes the wire format to three
        // values (CLAUDE.md §3), and the descriptor renders only those.
        var query = Query(text: "x", type: SearchTypeParser.Parse("<script>alert(1)</script>"));

        var reason = SearchQueryDescriptor.Describe(query);

        Assert.Equal("Query 'x' (search)", reason);
        Assert.DoesNotContain("script", reason, StringComparison.OrdinalIgnoreCase);
    }
}
