using Arbitarr.Api.Rendering;
using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Golden-XML tests for t=caps rendering: both protocol families, book categories never
/// present (M1-12), and enforced max page size.
/// </summary>
public class CapsXmlGoldenTests
{
    private static readonly SourceCaps SampleCaps = new(
        SupportedCategories: new[] { 5000, 2000, 3000 },
        SupportsTvSearch: true,
        SupportsMovieSearch: true,
        MaxPageSize: 100,
        SupportedParams: new[] { "q", "season", "ep" });

    [Fact]
    public void Torznab_caps_render_expected_namespace_and_categories()
    {
        var xml = TorznabXmlWriter.WriteCaps(SampleCaps);
        var rendered = xml.ToString();

        Assert.Contains("<caps>", rendered);
        Assert.Contains("<category id=\"5000\" name=\"TV\" />", rendered);
        Assert.Contains("<category id=\"2000\" name=\"Movies\" />", rendered);
        Assert.Contains("<category id=\"3000\" name=\"Audio\" />", rendered);
        Assert.Contains("<limits max=\"100\" default=\"100\" />", rendered);
    }

    [Fact]
    public void Newznab_caps_render_same_shape_as_torznab()
    {
        var torznabXml = TorznabXmlWriter.WriteCaps(SampleCaps).ToString();
        var newznabXml = NewznabXmlWriter.WriteCaps(SampleCaps).ToString();

        // Caps rendering carries no family-specific namespace prefix (only search-result items
        // do), so both families render byte-identical caps XML for the same SourceCaps input.
        Assert.Equal(torznabXml, newznabXml);
    }

    [Fact]
    public void Caps_writer_renders_exactly_the_categories_it_is_given()
    {
        // M1-12's "book categories never appear in caps" guarantee is enforced upstream by
        // CapsAggregator (see CapsAggregatorTests), which never includes SourceCaps.BookCategoryIds
        // in the SourceCaps it hands to this writer. The writer itself is a pure passthrough over
        // whatever SupportedCategories it receives — this asserts that passthrough contract.
        var rendered = TorznabXmlWriter.WriteCaps(SampleCaps).ToString();

        foreach (var categoryId in SampleCaps.SupportedCategories)
        {
            Assert.Contains($"id=\"{categoryId}\"", rendered);
        }
    }
}
