using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// #104 / CLAUDE.md §3: <see cref="SearchTypeParser"/> maps the inbound Newznab/Torznab <c>t=</c>
/// wire value to a <see cref="SearchType"/> by matching the NAMES explicitly, never via
/// <c>Enum.TryParse</c>.
///
/// <para>
/// The numeric-form cases below are the point of the file. <c>Enum.TryParse</c> accepts the
/// underlying numeric representation, so a parser written that way would mint
/// <see cref="SearchType.TvSearch"/> from <c>t=1</c> and <see cref="SearchType.Movie"/> from
/// <c>t=2</c> — an input shape no Newznab client is documented to send, and one that would let a
/// caller select a mode (and therefore which id/numbering parameters go upstream) through a
/// spelling the API never advertised. Neither guard people reach for first closes it:
/// <c>Enum.IsDefined</c> passes because <c>1</c> IS defined, and trimming does not help because
/// <c>" 1 "</c> and <c>"+1"</c> parse too. All three forms are pinned here.
/// </para>
///
/// <para>
/// The recognised-name cases are the positive control for those assertions: without them,
/// "everything maps to Search" would satisfy every numeric case just as happily while proving
/// nothing about whether the parser can select a mode at all.
/// </para>
/// </summary>
public class SearchTypeParserTests
{
    // Positive control: the parser really does select the two non-default modes.
    [Theory]
    [InlineData("tvsearch", SearchType.TvSearch)]
    [InlineData("TVSEARCH", SearchType.TvSearch)]
    [InlineData("TvSearch", SearchType.TvSearch)]
    [InlineData("  tvsearch  ", SearchType.TvSearch)]
    [InlineData("movie", SearchType.Movie)]
    [InlineData("MOVIE", SearchType.Movie)]
    public void Parse_MapsTheAdvertisedWireNames(string value, SearchType expected) =>
        Assert.Equal(expected, SearchTypeParser.Parse(value));

    [Theory]
    [InlineData("search")]
    [InlineData("SEARCH")]
    [InlineData("music")]
    [InlineData("book")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_TreatsAnythingElseAsAPlainSearch(string? value) =>
        Assert.Equal(SearchType.Search, SearchTypeParser.Parse(value));

    /// <summary>
    /// The §3 closure itself: none of the numeric spellings <c>Enum.TryParse</c> would accept
    /// selects a mode. <c>"1"</c>/<c>"2"</c> are the plain underlying values, <c>" 1 "</c> and
    /// <c>"+1"</c> are the two forms that survive the naive trimming/whitespace defences, and
    /// <c>"0"</c> is included so the assertion is not accidentally satisfied by Search's own
    /// underlying value being the expected answer for a reason other than the name match.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData(" 1 ")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("01")]
    public void Parse_NeverAcceptsTheNumericFormOfTheEnum(string value) =>
        Assert.Equal(SearchType.Search, SearchTypeParser.Parse(value));
}
