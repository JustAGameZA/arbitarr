using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Titles;
using Arbitarr.Media.Identity;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// Integration test only (no Arbitarr.Media production code changed): confirms that
/// <see cref="SeriesNameExtractor"/> (Arbitarr.Core.Identity) produces a bare series name that
/// <see cref="AlternateTitleMatcher"/> (Arbitarr.Media) — which does only trim + OrdinalIgnoreCase
/// equality against a <see cref="SeriesIdentity"/>'s titles — actually matches, for real raw release
/// titles pulled from docs/fixtures/nzbhydra.
/// </summary>
public class SeriesNameExtractorMatcherIntegrationTests
{
    private static SeriesIdentity Bleach => new(
        TvdbId: 74796,
        TmdbId: null,
        PrimaryTitle: "Bleach",
        AlternateTitles: []);

    // Mirrors GhostInTheShellFranchiseClassificationTests.StandAloneComplex, plus the bare
    // "Ghost In The Shell Stand Alone Complex" rendering (no colon) that raw release titles use -
    // added here, in the test's own identity object, per the standing instruction not to touch
    // Media source for a matcher-alias gap.
    private static SeriesIdentity StandAloneComplex => new(
        TvdbId: 78983,
        TmdbId: null,
        PrimaryTitle: "Ghost in the Shell: Stand Alone Complex",
        AlternateTitles: ["GitS: SAC", "Ghost In The Shell Stand Alone Complex"]);

    [Fact]
    public void Extract_RawBleachTitle_ThenMatcher_MatchesBleachIdentity()
    {
        // docs/fixtures/nzbhydra/bleach-tvsearch.xml item title.
        const string raw = "Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.NotNull(extracted);
        Assert.True(AlternateTitleMatcher.Matches(Bleach, extracted!));
    }

    [Fact]
    public void Extract_RawGhostInTheShellStandAloneComplexTitle_ThenMatcher_MatchesStandAloneComplexIdentity()
    {
        // docs/fixtures/nzbhydra/ghost-in-the-shell-generic.xml item title.
        const string raw = "Ghost In The Shell Stand Alone Complex S02E01 Di Reactivation Reembody EAC3 5 1 1080p Bluray x265-iVy";

        var extracted = SeriesNameExtractor.Extract(raw);

        Assert.NotNull(extracted);
        Assert.True(AlternateTitleMatcher.Matches(StandAloneComplex, extracted!));
    }
}
