using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-u1c: <see cref="SearchQuery.IsIdScopedAbsoluteNumberQuery"/> — the one predicate that
/// classifies Sonarr's anime episode shape.
/// </summary>
/// <remarks>
/// <para><b>WHY THIS FILE EXISTS RATHER THAN TWO SETS OF TESTS.</b> The question was briefly asked
/// by two private copies, one in <c>SearchEndpoint</c> and one in <c>NzbHydraSource</c>, on the
/// reasoning that they served different purposes. They drifted before either shipped: only one had
/// an overflow guard, and they disagreed about a <c>t=search</c> carrying a tvdbid. One side decides
/// the UPSTREAM URL and the other decides the CACHE KEY, so a disagreement caches one episode's
/// results under another's — which is the exact bug class arb-u1c set out to fix. The predicate now
/// lives on the query and both call it; these are its tests.</para>
///
/// <para>Every case below states which of the two behaviours it pins, so a future edit can tell what
/// it would be breaking.</para>
/// </remarks>
public class IdScopedAbsoluteNumberQueryTests
{
    private const int TvdbId = 81797;

    private static SearchQuery Query(
        string? queryText,
        int? tvdbId = TvdbId,
        SearchType type = SearchType.TvSearch) =>
        new(queryText, new[] { 5000 }, 50, SearchProtocol.Torznab, 0, TvdbId: tvdbId, Type: type);

    /// <summary>The shape itself: an id plus a bare absolute number, which is what Sonarr sends.</summary>
    [Theory]
    [InlineData("92", 92)]
    [InlineData("092", 92)]
    [InlineData("  92  ", 92)]
    [InlineData("0", 0)]
    [InlineData("00000", 0)]
    [InlineData("2147483647", int.MaxValue)]
    public void Recognises_a_bare_absolute_number_beside_a_tvdb_id(string queryText, int expected)
    {
        Assert.True(Query(queryText).IsIdScopedAbsoluteNumberQuery(out var absolute));
        Assert.Equal(expected, absolute);
    }

    /// <summary>
    /// The overflow guard, and the half that had drifted: a run of digits too long to be an
    /// <see cref="int"/> is not an episode number, it is numeric-looking text. Answering false sends
    /// it upstream as an ordinary <c>q</c> (correct for text) and records no absolute in the cache
    /// key (correct — a nonsense value there separates rows that should share one).
    /// </summary>
    [Theory]
    [InlineData("2147483648")]
    [InlineData("99999999999999999999")]
    public void Rejects_a_digit_run_too_long_to_be_an_episode_number(string queryText)
    {
        Assert.False(Query(queryText).IsIdScopedAbsoluteNumberQuery(out var absolute));
        Assert.Equal(0, absolute);
    }

    /// <summary>
    /// A textual q beside an id is a title and stays — the predicate must not swallow
    /// <c>tvdbid=74796&amp;q=bleach</c>.
    /// </summary>
    [Theory]
    [InlineData("bleach")]
    [InlineData("One Piece 92")]
    [InlineData("9 2")]
    [InlineData("92a")]
    [InlineData("-92")]
    [InlineData("+92")]
    [InlineData("9.2")]
    public void Rejects_anything_that_is_not_entirely_digits(string queryText)
    {
        // Every case here keeps a non-digit AFTER trimming, so each one genuinely exercises the
        // rejection. Surrounding whitespace alone does NOT belong in this list — "  92  " trims to
        // the real shape and is asserted as recognised above — and "-92"/"+92" are here because
        // int.TryParse would accept both under looser NumberStyles: the predicate must reject them
        // on the digits-only check before parsing is ever reached.
        Assert.False(Query(queryText).IsIdScopedAbsoluteNumberQuery(out _));
    }

    /// <summary>
    /// Non-ASCII numerals are text like any other. Sonarr formats the number with the invariant
    /// culture, so only ASCII digits are the shape being matched.
    /// </summary>
    [Fact]
    public void Rejects_non_ascii_numerals()
    {
        Assert.False(Query("٩٢").IsIdScopedAbsoluteNumberQuery(out _));
    }

    /// <summary>
    /// A numeric q with NO id has nothing else to identify the series, so it stays: dropping it
    /// would turn the request into a category-wide feed.
    /// </summary>
    [Fact]
    public void Rejects_a_bare_number_with_no_tvdb_id()
    {
        Assert.False(Query("92", tvdbId: null).IsIdScopedAbsoluteNumberQuery(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_an_absent_query_text(string? queryText)
    {
        Assert.False(Query(queryText).IsIdScopedAbsoluteNumberQuery(out _));
    }

    /// <summary>
    /// <b>THE CASE THE TWO COPIES DISAGREED ABOUT.</b> <c>NzbHydraSource.SearchMode</c> maps a
    /// tvdbid-bearing query to <c>t=tvsearch</c> whatever its inbound <c>t=</c> said, so the source
    /// treated this as the anime shape and withheld the number; the endpoint's copy keyed off the
    /// parsed <see cref="SearchType"/> alone and recorded no absolute for it. The upstream URL and
    /// the cache key were therefore built from different answers about the same request. One
    /// predicate, and it answers the way the URL is actually built.
    /// </summary>
    [Fact]
    public void Recognises_the_shape_when_the_inbound_mode_was_search_but_a_tvdb_id_is_present()
    {
        Assert.True(
            Query("92", type: SearchType.Search).IsIdScopedAbsoluteNumberQuery(out var absolute));
        Assert.Equal(92, absolute);
    }

    /// <summary>
    /// A movie search is never this shape, even carrying a tvdbid: the source sends it as
    /// <c>t=movie</c>, where the withhold branch does not apply at all.
    /// </summary>
    [Fact]
    public void Rejects_a_movie_search()
    {
        Assert.False(Query("92", type: SearchType.Movie).IsIdScopedAbsoluteNumberQuery(out _));
    }

    /// <summary>
    /// The out parameter is not to be read when the answer is false: it is left at zero, which is
    /// also a VALID absolute number, so a caller trusting it unconditionally would record absolute 0
    /// for every ordinary text search. Pinned so the two-value contract stays explicit.
    /// </summary>
    [Fact]
    public void Reports_zero_and_false_together_for_a_non_matching_query()
    {
        Assert.False(Query("bleach").IsIdScopedAbsoluteNumberQuery(out var absolute));
        Assert.Equal(0, absolute);

        Assert.True(Query("0").IsIdScopedAbsoluteNumberQuery(out var zero));
        Assert.Equal(0, zero);
    }
}
