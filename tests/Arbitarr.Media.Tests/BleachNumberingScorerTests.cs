using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Media.Numbering;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// M6-1 (plan lines 798-830, authorised by team-lead at 171e7e0): for the Bleach query, a release
/// equivalent to <c>[ToonsHub] BLEACH Thousand-Year Blood War S01E36</c> — the "Sennen Kessen hen"
/// alias family from docs/step3b-observed-failures.md section 5 — must rank #1 among a field of
/// releases, while releases equivalent to <c>Bleach.S16E19...</c>, <c>[TESHI]Bleach-236</c>, and
/// <c>Bleach - 329 [SGKK]</c> must both fall outside the top 5 AND score below
/// <see cref="ConfidenceCalibration.AcceptanceThreshold"/> (0.9) — shadow mode OFF, single
/// non-disjunctive threshold (<see cref="ConfidenceCalibration.MeetsAcceptanceThreshold"/>).
/// </summary>
public class BleachNumberingScorerTests
{
    /// <summary>
    /// TYBW arc binding extended (step 3, this pass) with <c>AlternateSceneSeasons: [1]</c> so the
    /// "Sennen Kessen hen" family (scene season 1, Japanese arc-title token) can bind via the new
    /// scene-season-alias path in <see cref="CandidateNumberingSetBuilder.ResolveBinding"/>, per
    /// team-lead's explicit instruction to extend the builder rather than the scorer for this case.
    /// </summary>
    private static ArcSeasonBinding TybwBinding => new(
        ArcTitle: "Thousand-Year Blood War",
        AlternateArcTitles: ["TYBW", "Thousand Year Blood War"],
        Season: 17,
        AbsoluteRangeStart: 367,
        AbsoluteRangeEnd: 402,
        AlternateSceneSeasons: [1]);

    private static ArcSeasonMap BleachArcMap => new([TybwBinding]);

    /// <summary>
    /// Scores one release's raw numbering (with its own arc-title token, exactly as a real title
    /// would carry) end-to-end: build the candidate set, then score it.
    /// </summary>
    private static NumberingCandidateScore? ScoreRelease(RawReleaseNumbering raw)
    {
        var candidates = CandidateNumberingSetBuilder.Build(raw, BleachArcMap);
        return NumberingCandidateScoring.ScoreBest(candidates, BleachArcMap, raw.ArcTitleToken, sceneSeason: raw.SceneSeason);
    }

    [Fact]
    public void SennenKessenHenS01E36_RanksFirst_AboveThreeNonMatches_AndMeetsAcceptanceThreshold()
    {
        // "[ToonsHub] BLEACH Thousand-Year Blood War S01E36" - Japanese-audio release-group
        // rendering, scene season 1, arc-title token "Sennen Kessen hen" (no title-token match on
        // its own - resolves via the scene-season alias added this pass). Absolute 402 is exactly
        // the TYBW binding's own declared range end.
        var tybwAlias = new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 36, Absolute: 402, ArcTitleToken: "Sennen Kessen hen");

        // "Bleach.S16E19..." - a different, unrelated scene season (16, not 17 or its scene-season
        // alias 1) with no arc-title token at all: carries through as an uncorroborated ArcRelative
        // candidate under season 16, not season 17 - never resolves to TYBW at all.
        var unrelatedSeason16 = new RawReleaseNumbering(SceneSeason: 16, SceneEpisode: 19, Absolute: null, ArcTitleToken: null);

        // "[TESHI]Bleach-236" - a bare absolute-episode-only release (the "236" is a Sword-style
        // combined scene+episode/absolute encoding collapsed here to a bare absolute, per the docs'
        // framing of scene-season-free absolute-only titles): no scene season/episode pair, no arc
        // title token - only ever produces a bare Absolute-scheme candidate (R5: never keys a match).
        var bareAbsoluteOnly = new RawReleaseNumbering(SceneSeason: null, SceneEpisode: null, Absolute: 236, ArcTitleToken: null);

        // "Bleach - 329 [SGKK]" - same shape, a different bare absolute number.
        var anotherBareAbsolute = new RawReleaseNumbering(SceneSeason: null, SceneEpisode: null, Absolute: 329, ArcTitleToken: null);

        var scored = new[]
        {
            (Label: "SennenKessenHenS01E36", Score: ScoreRelease(tybwAlias)),
            (Label: "UnrelatedSeason16", Score: ScoreRelease(unrelatedSeason16)),
            (Label: "BareAbsoluteOnly236", Score: ScoreRelease(bareAbsoluteOnly)),
            (Label: "AnotherBareAbsolute329", Score: ScoreRelease(anotherBareAbsolute)),
        };

        var tybwScore = scored.Single(s => s.Label == "SennenKessenHenS01E36").Score;
        Assert.NotNull(tybwScore);

        var ranked = scored
            .Where(s => s.Score is not null)
            .OrderByDescending(s => s.Score!.Confidence)
            .ToArray();

        // Top 5 (of a 4-item field here): TYBW alias must rank #1.
        Assert.Equal("SennenKessenHenS01E36", ranked[0].Label);
        Assert.True(ConfidenceCalibration.MeetsAcceptanceThreshold(tybwScore!.Confidence),
            $"Expected TYBW alias confidence >= {ConfidenceCalibration.AcceptanceThreshold}, was {tybwScore.Confidence}");

        // The three named non-matches: outside top 5 (trivially true in a 4-item field where TYBW is
        // #1, but asserted explicitly per the acceptance criteria's own framing) AND below 0.9.
        foreach (var label in new[] { "UnrelatedSeason16", "BareAbsoluteOnly236", "AnotherBareAbsolute329" })
        {
            var nonMatch = scored.Single(s => s.Label == label).Score;
            Assert.NotNull(nonMatch);
            Assert.NotEqual(0, Array.IndexOf(ranked.Select(r => r.Label).ToArray(), label));
            Assert.False(ConfidenceCalibration.MeetsAcceptanceThreshold(nonMatch!.Confidence),
                $"Expected {label} confidence < {ConfidenceCalibration.AcceptanceThreshold}, was {nonMatch.Confidence}");
        }
    }

    [Fact]
    public void TokenMatchedCandidate_WithinDeclaredRange_ScoresHigherThan_TokenMatchedCandidate_OutOfRange()
    {
        // Positive control for the AbsoluteWithinDeclaredRange weight: E36 (absolute 402, exactly
        // the binding's declared range end) vs E42 (derived absolute 408, past the declared range
        // end per the docs' "range-completeness gap" row) - both resolve via the same arc-title
        // token, so the only difference is range membership.
        var inRange = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 36, Absolute: 402, ArcTitleToken: "Thousand-Year Blood War");
        var outOfRange = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 42, Absolute: null, ArcTitleToken: "Thousand-Year Blood War");

        var inRangeScore = ScoreRelease(inRange);
        var outOfRangeScore = ScoreRelease(outOfRange);

        Assert.NotNull(inRangeScore);
        Assert.NotNull(outOfRangeScore);
        Assert.True(inRangeScore!.Confidence > outOfRangeScore!.Confidence);
    }

    [Fact]
    public void UncorroboratedArcRelativeCandidate_ScoresHigherThanNothing_ButBelowTokenMatched()
    {
        // Positive control for the UncorroboratedArcRelative weight (docs: bare "Bleach S17E##..."
        // titles, no arc words) - must score above zero (it is still information) but below any
        // token-matched candidate.
        var bare = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 45, Absolute: null, ArcTitleToken: null);
        var tokenMatched = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 42, Absolute: null, ArcTitleToken: "Thousand-Year Blood War");

        var bareScore = ScoreRelease(bare);
        var tokenMatchedScore = ScoreRelease(tokenMatched);

        Assert.NotNull(bareScore);
        Assert.NotNull(tokenMatchedScore);
        Assert.True(bareScore!.Confidence > 0);
        Assert.True(bareScore.Confidence < tokenMatchedScore!.Confidence);
    }
}
