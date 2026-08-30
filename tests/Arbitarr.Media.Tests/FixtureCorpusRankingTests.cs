using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Media.Numbering;
using Arbitarr.Media.Ranking;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// M6-4 / M6-5 / M6-6 (plan lines 846-863): the step-5 ranking runs over every captured NZBHydra
/// fixture at the single 0.9 acceptance threshold without throwing, no known-wrong title reaches
/// the threshold, an empty upstream page yields a clean empty ranking, and losing XEM /
/// Anime-Lists degrades to similarity-only ordering with the reason recorded (P3).
/// </summary>
public class FixtureCorpusRankingTests
{
    private static SeriesIdentity Bleach => new(74796, null, "Bleach", ["BLEACH Sennen Kessen hen", "Bleach: Thousand-Year Blood War"]);
    private static SeriesIdentity Arise => new(264492, null, "Ghost in the Shell: Arise", ["GitS: Arise"]);
    private static SeriesIdentity StandAloneComplex => new(78983, null, "Ghost in the Shell: Stand Alone Complex", ["GitS: SAC"]);
    private static SeriesIdentity Sac2045 => new(361034, null, "Ghost in the Shell: SAC_2045", []);

    private static ArcSeasonBinding TybwBinding => new(
        ArcTitle: "Thousand-Year Blood War",
        AlternateArcTitles: ["TYBW", "Thousand Year Blood War"],
        Season: 17,
        AbsoluteRangeStart: 367,
        AbsoluteRangeEnd: 402,
        AlternateSceneSeasons: [1]);

    private static RankingContext BleachContext(ArcSeasonMap? arcMap, IReadOnlyList<string>? degradations = null) =>
        new(Bleach, [Bleach], arcMap, degradations);

    private static RankingContext GitsContext(SeriesIdentity requested) =>
        new(requested, [Arise, StandAloneComplex, Sac2045], ArcMap: null);

    /// <summary>
    /// Titles docs/step3b-observed-failures.md names as known-wrong for a TYBW query. None appears
    /// in any captured fixture, so they are injected into every corpus page to prove the threshold
    /// rejects them alongside the real data.
    /// </summary>
    private static readonly string[] KnownWrongBleachTitles =
    [
        "Bleach.S16E19",
        "[TESHI]Bleach-236",
        "Bleach - 329 [SGKK]",
    ];

    private static IEnumerable<string> FixtureFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.xml")
            .Select(Path.GetFileName)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal);

    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in FixtureFiles())
        {
            data.Add(file);
        }

        return data;
    }

    [Fact]
    public void CorpusIsPresent()
    {
        Assert.True(FixtureFiles().Count() >= 8, "expected every docs/fixtures/nzbhydra capture to be copied next to the tests");
    }

    /// <summary>M6-4: ranking every fixture at threshold 0.9 never throws and never admits a known-wrong title.</summary>
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void M6_4_EveryFixture_RanksWithoutThrowing_AndNoKnownWrongTitleMeetsThreshold(string fixture)
    {
        var titles = GhostInTheShellDeRankingTests.LoadFixtureTitles(fixture);
        var isGits = fixture.StartsWith("ghost-in-the-shell", StringComparison.Ordinal);

        var corpus = titles.Concat(KnownWrongBleachTitles).ToArray();
        var context = isGits ? GitsContext(StandAloneComplex) : BleachContext(new ArcSeasonMap([TybwBinding]));

        var result = ReleaseRanking.Rank(corpus, context);

        Assert.Equal(corpus.Length, result.Ranked.Count);
        Assert.All(result.Ranked, r => Assert.Equal(ConfidenceCalibration.MeetsAcceptanceThreshold(r.Confidence), r.MeetsAcceptanceThreshold));

        foreach (var wrong in KnownWrongBleachTitles)
        {
            var entry = Assert.Single(result.Ranked, r => r.Release.Title == wrong);
            Assert.False(entry.MeetsAcceptanceThreshold, $"{wrong} reached {entry.Confidence:F2} in {fixture}");
        }

        // A SAC_2045 release must never satisfy the threshold for a Stand Alone Complex query.
        Assert.DoesNotContain(
            result.Ranked,
            r => r.MeetsAcceptanceThreshold && r.Release.Title.Contains("2045", StringComparison.Ordinal));
    }

    /// <summary>M6-4 positive control: the threshold is reachable by a genuinely corroborated TYBW release.</summary>
    [Fact]
    public void M6_4_PositiveControl_CorroboratedTybwRelease_MeetsThreshold()
    {
        var titles = GhostInTheShellDeRankingTests.LoadFixtureTitles("bleach-tvsearch.xml");
        var aliasRelease = titles.First(t => t.Contains("Sennen Kessen hen S01E", StringComparison.OrdinalIgnoreCase));

        var result = ReleaseRanking.Rank(titles.Concat(KnownWrongBleachTitles).ToArray(), BleachContext(new ArcSeasonMap([TybwBinding])));

        var entry = Assert.Single(result.Ranked, r => r.Release.Title == aliasRelease);
        Assert.True(entry.MeetsAcceptanceThreshold, $"{aliasRelease} scored {entry.Confidence:F2}");
        Assert.Equal(0.95, entry.Confidence, precision: 10);
        Assert.Equal(17, entry.BestNumbering!.Candidate.Season);
    }

    /// <summary>M6-5: the honest zero-result One Piece capture yields a clean empty ranking, no synthetic entries.</summary>
    [Fact]
    public void M6_5_OnePieceZeroResults_YieldsCleanEmptyRanking()
    {
        var onePiece = new SeriesIdentity(81797, null, "One Piece", []);
        var titles = GhostInTheShellDeRankingTests.LoadFixtureTitles("ac10-sweep-onepiece-zero-results.xml");
        Assert.Empty(titles);

        var result = ReleaseRanking.Rank(titles, new RankingContext(onePiece, [onePiece], ArcMap: null));

        Assert.Empty(result.Ranked);
        Assert.Empty(result.Degradations);
    }

    /// <summary>
    /// M6-6: with XEM and Anime-Lists unreachable there is no arc map, so no numbering can be
    /// corroborated. Ranking still produces a non-empty, ordered result on title similarity alone,
    /// records the upstream reasons plus its own similarity-only degradation (P3), and admits nothing
    /// through the 0.9 threshold.
    /// </summary>
    [Fact]
    public void M6_6_MetadataUnreachable_DegradesToSimilarityOnly_AndRecordsReason()
    {
        var titles = GhostInTheShellDeRankingTests.LoadFixtureTitles("bleach-tvsearch.xml");
        string[] upstream = ["XEM unreachable", "Anime-Lists unreachable"];

        var result = ReleaseRanking.Rank(titles, BleachContext(arcMap: null, degradations: upstream));

        Assert.Equal(titles.Count, result.Ranked.Count);
        for (var i = 1; i < result.Ranked.Count; i++)
        {
            Assert.True(result.Ranked[i - 1].Confidence >= result.Ranked[i].Confidence);
        }

        Assert.Contains("XEM unreachable", result.Degradations);
        Assert.Contains("Anime-Lists unreachable", result.Degradations);
        Assert.Contains(ReleaseRanker.SimilarityOnlyDegradation, result.Degradations);

        Assert.All(result.Ranked, r => Assert.False(r.MeetsAcceptanceThreshold));
        Assert.True(result.Ranked[0].Confidence > 0);
    }

    /// <summary>M6-6 contrast: the same page with the arc map present is not similarity-only.</summary>
    [Fact]
    public void M6_6_MetadataAvailable_DoesNotRecordSimilarityOnlyDegradation()
    {
        var titles = GhostInTheShellDeRankingTests.LoadFixtureTitles("bleach-tvsearch.xml");

        var result = ReleaseRanking.Rank(titles, BleachContext(new ArcSeasonMap([TybwBinding])));

        Assert.DoesNotContain(ReleaseRanker.SimilarityOnlyDegradation, result.Degradations);
        Assert.Contains(result.Ranked, r => r.MeetsAcceptanceThreshold);
    }
}
