using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Media.Numbering;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// R5 hardening of the scene-season-alias binding tier (verifier finding D1 on PR #30): a bare
/// <c>SxxEyy</c> release with no arc-title token may bind to an arc ONLY through a declared,
/// unambiguous <see cref="ArcSeasonBinding.AlternateSceneSeasons"/> alias. Binding through a
/// binding's own <see cref="ArcSeasonBinding.Season"/>, or through an alias two bindings both claim,
/// would let the ORDER of the map decide the arc and fabricate an in-range absolute for a release
/// that never named it - a confident-but-wrong binding R5 forbids.
/// </summary>
public class SceneSeasonAliasBindingTests
{
    private static ArcSeasonBinding Arrancar => new(
        ArcTitle: "Arrancar",
        AlternateArcTitles: [],
        Season: 6,
        AbsoluteRangeStart: 110,
        AbsoluteRangeEnd: 167);

    /// <summary>TYBW deliberately declares scene season 6 as an alias so it collides with Arrancar's own Season.</summary>
    private static ArcSeasonBinding TybwClaimingSeason6 => new(
        ArcTitle: "Thousand-Year Blood War",
        AlternateArcTitles: ["TYBW"],
        Season: 17,
        AbsoluteRangeStart: 367,
        AbsoluteRangeEnd: 402,
        AlternateSceneSeasons: [6]);

    private static ArcSeasonBinding Tybw => TybwClaimingSeason6 with { AlternateSceneSeasons = [1] };

    private static RawReleaseNumbering BareS06E03 => new(SceneSeason: 6, SceneEpisode: 3, Absolute: null, ArcTitleToken: null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnSeasonCollidingWithAlias_BindsNothing_RegardlessOfBindingOrder(bool arrancarFirst)
    {
        var map = new ArcSeasonMap(arrancarFirst ? [Arrancar, TybwClaimingSeason6] : [TybwClaimingSeason6, Arrancar]);

        Assert.Null(map.FindBySceneSeasonAlias(6));

        var set = CandidateNumberingSetBuilder.Build(BareS06E03, map);
        var candidate = Assert.Single(set.Candidates);
        Assert.Equal(NumberingScheme.ArcRelative, candidate.Scheme);
        Assert.Equal(6, candidate.Season);
        Assert.Equal(3, candidate.Episode);
        Assert.Null(candidate.Absolute);

        var best = NumberingCandidateScoring.ScoreBest(set, map, arcTitleToken: null, sceneSeason: 6);
        Assert.NotNull(best);
        Assert.False(ConfidenceCalibration.MeetsAcceptanceThreshold(best!.Confidence), $"bare S06E03 reached {best.Confidence:F2}");
    }

    [Fact]
    public void BindingOrder_NeverChangesTheCandidateSet()
    {
        var forward = CandidateNumberingSetBuilder.Build(BareS06E03, new ArcSeasonMap([Arrancar, TybwClaimingSeason6]));
        var reverse = CandidateNumberingSetBuilder.Build(BareS06E03, new ArcSeasonMap([TybwClaimingSeason6, Arrancar]));

        Assert.Equal(forward.Candidates, reverse.Candidates);
        Assert.DoesNotContain(forward.Candidates, c => c.Season == 17 || c.Absolute == 369);
    }

    [Fact]
    public void AliasClaimedByTwoBindings_BindsNothing()
    {
        var soulSociety = new ArcSeasonBinding("Entry: Soul Society", [], Season: 2, AbsoluteRangeStart: 21, AbsoluteRangeEnd: 63, AlternateSceneSeasons: [1]);
        var map = new ArcSeasonMap([soulSociety, Tybw]);

        Assert.Null(map.FindBySceneSeasonAlias(1));

        var set = CandidateNumberingSetBuilder.Build(new RawReleaseNumbering(1, 36, null, null), map);

        // R5: an unresolved scene season 1 is never carried through as a bare (Season: 1, ep) candidate.
        Assert.Empty(set.Candidates);
    }

    [Fact]
    public void OwnSeason_IsNotAnAlias_BareS17CarriesThroughWithoutFabricatedAbsolute()
    {
        var map = new ArcSeasonMap([Tybw]);

        Assert.False(Tybw.IsAlternateSceneSeason(17));
        Assert.Null(map.FindBySceneSeasonAlias(17));

        var set = CandidateNumberingSetBuilder.Build(new RawReleaseNumbering(17, 3, null, null), map);
        var candidate = Assert.Single(set.Candidates);
        Assert.Equal(17, candidate.Season);
        Assert.Equal(3, candidate.Episode);
        Assert.Null(candidate.Absolute);

        var corroboration = Assert.Single(NumberingCandidateScoring.BuildCorroborations(set, map, arcTitleToken: null, sceneSeason: 17));
        Assert.False(corroboration.ArcSceneSeasonAliasMatched);
        Assert.False(corroboration.ArcTitleTokenMatched);
    }

    [Fact]
    public void GenuineUniqueAlias_StillResolves_AndIsCorroborated()
    {
        var map = new ArcSeasonMap([Arrancar, Tybw]);

        Assert.Equal(Tybw.ArcTitle, map.FindBySceneSeasonAlias(1)!.ArcTitle);

        var raw = new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 36, Absolute: 402, ArcTitleToken: "Sennen Kessen hen");
        var set = CandidateNumberingSetBuilder.Build(raw, map);
        var best = NumberingCandidateScoring.ScoreBest(set, map, raw.ArcTitleToken, sceneSeason: raw.SceneSeason);

        Assert.NotNull(best);
        Assert.Equal(17, best!.Candidate.Season);
        Assert.Equal(402, best.Candidate.Absolute);
        Assert.True(ConfidenceCalibration.MeetsAcceptanceThreshold(best.Confidence));
    }
}
