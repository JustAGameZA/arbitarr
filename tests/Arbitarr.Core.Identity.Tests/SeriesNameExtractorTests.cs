using Arbitarr.Core.Identity.Titles;
using Xunit;

namespace Arbitarr.Core.Identity.Tests;

/// <summary>
/// M5/M6 follow-up: <see cref="SeriesNameExtractor"/> reduces a raw release title down to a bare
/// series name so it can be exact-matched (trim + case-insensitive) against a
/// <c>SeriesIdentity</c>'s titles. Table-driven over real fixture titles from
/// docs/fixtures/nzbhydra/bleach-tvsearch.xml and docs/fixtures/nzbhydra/ghost-in-the-shell-generic.xml,
/// plus representative release-group-tagged and absolute-numbered formats.
/// </summary>
public class SeriesNameExtractorTests
{
    public static IEnumerable<object?[]> Cases =>
        new List<object?[]>
        {
            // docs/fixtures/nzbhydra/bleach-tvsearch.xml
            new object?[] { "Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB", "Bleach" },
            new object?[] { "Bleach S17E44 THE PERFECT CRIMSON XviD-AFG", "Bleach" },
            new object?[] { "Bleach.S17E44.THE.PERFECT.CRIMSON.720p.HEVC.x265-MeGusta", "Bleach" },
            new object?[] { "BLEACH Sennen Kessen hen S01E33 2024 1080p Baha WEB-DL x264 AAC-ADWeb", "BLEACH Sennen Kessen hen" },
            new object?[] { "BLEACH Thousand-Year Blood War S03E10 1080p WEB H264-KAWAII", "BLEACH Thousand-Year Blood War" },
            new object?[] { "BLEACH Thousand-Year Blood War S01 JAPANESE 720p DSNP WEBRip AAC2 0 x264-NTb", "BLEACH Thousand-Year Blood War" },

            // Release-group tag + absolute-episode-number formats (no season/episode marker).
            new object?[] { "[ToonsHub] BLEACH Thousand-Year Blood War S01E36", "BLEACH Thousand-Year Blood War" },
            new object?[] { "Bleach - 329 [SGKK]", "Bleach" },
            new object?[] { "[TESHI]Bleach-236", "Bleach" },

            // docs/fixtures/nzbhydra/ghost-in-the-shell-generic.xml
            new object?[]
            {
                "Ghost In The Shell Stand Alone Complex S02E01 Di Reactivation Reembody EAC3 5 1 1080p Bluray x265-iVy",
                "Ghost In The Shell Stand Alone Complex",
            },
            new object?[] { "Ghost in the Shell SAC 2045 S02E13 1080p WEB H264-SUGOI", "Ghost in the Shell SAC 2045" },
            new object?[] { "Ghost in the Shell SAC2045 S01 JAPANESE 1080p WEBRip x265", "Ghost in the Shell SAC2045" },

            // Edge cases.
            new object?[] { "  Bleach  ", "Bleach" },
            new object?[] { "   ", null },
            new object?[] { "[SGKK]", null },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Extract_RawReleaseTitle_ReturnsExpectedBareSeriesName(string raw, string? expected)
    {
        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.Equal(expected, extracted);
    }
}
