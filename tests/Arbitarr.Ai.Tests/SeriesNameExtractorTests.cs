using Arbitarr.Ai.Normalization;
using Xunit;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// M5 normalizer follow-up: <c>Arbitarr.Media.Identity.AlternateTitleMatcher</c> does only trim +
/// exact case-insensitive equality against a <c>SeriesIdentity</c>'s titles — it applies no
/// stripping of its own. <see cref="SeriesNameExtractor"/> closes that gap by reducing a raw release
/// title down to a bare series name before it reaches the matcher. These tests use real raw titles
/// pulled from docs/fixtures/nzbhydra/bleach-tvsearch.xml and
/// docs/fixtures/nzbhydra/ghost-in-the-shell-generic.xml, and assert the extracted result would
/// satisfy AlternateTitleMatcher's trim + OrdinalIgnoreCase equality contract against the
/// corresponding SeriesIdentity title (Arbitarr.Ai must not reference Arbitarr.Media per AC6a, so
/// the equality check here is reproduced directly rather than calling the matcher).
/// </summary>
public class SeriesNameExtractorTests
{
    [Fact]
    public void Extract_RawBleachTitle_ReturnsBareSeriesName()
    {
        // docs/fixtures/nzbhydra/bleach-tvsearch.xml item title.
        const string raw = "Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.Equal("Bleach", extracted);
    }

    [Fact]
    public void Extract_RawBleachTitle_SatisfiesAlternateTitleMatcherEqualityContract()
    {
        const string raw = "Bleach S17E44 THE PERFECT CRIMSON XviD-AFG";
        const string primaryTitle = "Bleach";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.Equal(primaryTitle.Trim(), extracted.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_RawGhostInTheShellStandAloneComplexTitle_ReturnsBareSeriesName()
    {
        // docs/fixtures/nzbhydra/ghost-in-the-shell-generic.xml item title.
        const string raw = "Ghost In The Shell Stand Alone Complex S02E01 Di Reactivation Reembody EAC3 5 1 1080p Bluray x265-iVy";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.Equal("Ghost In The Shell Stand Alone Complex", extracted);
    }

    [Fact]
    public void Extract_RawGhostInTheShellSac2045Title_SatisfiesAlternateTitleMatcherEqualityContract()
    {
        // docs/fixtures/nzbhydra/ghost-in-the-shell-generic.xml item title (SAC_2045 sub-series).
        const string raw = "Ghost in the Shell SAC 2045 S02E13 1080p WEB H264-SUGOI";
        const string alternateTitle = "Ghost in the Shell SAC 2045";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.Equal(alternateTitle.Trim(), extracted.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_TitleWithNoEpisodeMarker_ReturnsTrimmedInputUnchanged()
    {
        const string raw = "  Bleach  ";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.Equal("Bleach", extracted);
    }

    [Fact]
    public void Extract_DotSeparatedRawTitle_ReturnsSpaceJoinedSeriesName()
    {
        const string raw = "Bleach.S17E44.THE.PERFECT.CRIMSON.720p.HEVC.x265-MeGusta";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.Equal("Bleach", extracted);
    }
}
