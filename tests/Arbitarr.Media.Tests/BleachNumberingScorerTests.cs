using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Media.Numbering;
using Arbitarr.Media.Ranking;
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
/// The fixture field below carries 8 candidates (the 3 named non-matches plus 4 additional decoys)
/// so the "outside top 5" cut has teeth — with only 4 items in the field it would be vacuously true
/// for every non-#1 entry the moment TYBW ranks first; with 8 items, there are exactly 3 index slots
/// (5, 6, 7) outside a top-5 cut, one for each of the 3 named non-matches. The below-0.9 confidence
/// assertion remains an independent, non-vacuous gate on top of that (see the in-test comment).
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

        // Four additional decoys (not named in the acceptance criteria, added so the "outside top 5"
        // cut below has teeth in a field larger than 5): legitimate TYBW token-matched candidates
        // whose derived absolute falls outside the binding's declared range (per the docs'
        // "range-completeness gap" row), so each scores ArcTitleTokenMatch alone (0.6) - strictly
        // above the 3 named non-matches' 0.1/0.05 tier, but below TYBW alias's 0.6+0.25 in-range
        // score - guaranteeing the 3 named non-matches rank last (indices 5-7 of this 8-item field)
        // regardless of ordering among same-tier ties.
        var outOfRangeTokenMatch1 = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 40, Absolute: null, ArcTitleToken: "Thousand-Year Blood War");
        var outOfRangeTokenMatch2 = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 41, Absolute: null, ArcTitleToken: "Thousand-Year Blood War");
        var outOfRangeTokenMatch3 = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 43, Absolute: null, ArcTitleToken: "Thousand-Year Blood War");
        var outOfRangeTokenMatch4 = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 44, Absolute: null, ArcTitleToken: "Thousand-Year Blood War");

        var scored = new[]
        {
            (Label: "SennenKessenHenS01E36", Score: ScoreRelease(tybwAlias)),
            (Label: "UnrelatedSeason16", Score: ScoreRelease(unrelatedSeason16)),
            (Label: "BareAbsoluteOnly236", Score: ScoreRelease(bareAbsoluteOnly)),
            (Label: "AnotherBareAbsolute329", Score: ScoreRelease(anotherBareAbsolute)),
            (Label: "OutOfRangeTokenMatch1", Score: ScoreRelease(outOfRangeTokenMatch1)),
            (Label: "OutOfRangeTokenMatch2", Score: ScoreRelease(outOfRangeTokenMatch2)),
            (Label: "OutOfRangeTokenMatch3", Score: ScoreRelease(outOfRangeTokenMatch3)),
            (Label: "OutOfRangeTokenMatch4", Score: ScoreRelease(outOfRangeTokenMatch4)),
        };

        var tybwScore = scored.Single(s => s.Label == "SennenKessenHenS01E36").Score;
        Assert.NotNull(tybwScore);

        var ranked = scored
            .Where(s => s.Score is not null)
            .OrderByDescending(s => s.Score!.Confidence)
            .ToArray();

        // Top 5 of an 8-item field: TYBW alias must rank #1.
        Assert.Equal("SennenKessenHenS01E36", ranked[0].Label);
        Assert.True(ConfidenceCalibration.MeetsAcceptanceThreshold(tybwScore!.Confidence),
            $"Expected TYBW alias confidence >= {ConfidenceCalibration.AcceptanceThreshold}, was {tybwScore.Confidence}");

        var rankedLabels = ranked.Select(r => r.Label).ToArray();

        // The three named non-matches: outside top 5 AND below 0.9. With 8 candidates in the field
        // (the 3 named non-matches plus 4 additional decoys), "outside top 5" is now a real boundary
        // a regression could actually fail (index >= 5, not merely != 0) - it is no longer trivially
        // satisfied just because TYBW is #1, and there are exactly 3 slots (5, 6, 7) for the 3 named
        // non-matches to occupy. The MeetsAcceptanceThreshold(...) < 0.9 assertion remains an
        // independent, non-vacuous gate on top of that.
        foreach (var label in new[] { "UnrelatedSeason16", "BareAbsoluteOnly236", "AnotherBareAbsolute329" })
        {
            var nonMatch = scored.Single(s => s.Label == label).Score;
            Assert.NotNull(nonMatch);
            Assert.True(Array.IndexOf(rankedLabels, label) >= 5,
                $"Expected {label} to rank outside the top 5, was at index {Array.IndexOf(rankedLabels, label)}");
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

    private static SeriesIdentity Bleach => new(74796, null, "Bleach", ["BLEACH Sennen Kessen hen", "Bleach: Thousand-Year Blood War"]);

    /// <summary>
    /// M6-1 end to end over the literal titles from docs/step3b-observed-failures.md, through
    /// <see cref="ReleaseRanking.Rank"/> (parser → identity resolution → candidate set → scorer →
    /// ranker) rather than hand-built <see cref="RawReleaseNumbering"/> records: the TYBW releases
    /// clear the 0.9 threshold and rank first, the three known-wrong Bleach titles stay below it,
    /// and a foreign series that borrows the arc words never resolves to Bleach at all.
    /// </summary>
    [Fact]
    public void LiteralTitles_ThroughReleaseRanking_TybwMeetsThreshold_KnownWrongAndForeignSeriesDoNot()
    {
        const string toonsHub = "[ToonsHub] BLEACH Thousand-Year Blood War S01E36 1080p WEB-DL AAC x264";
        const string sennenKessenHen = "BLEACH Sennen Kessen hen S01E36 1080p WEB-DL x264";
        string[] knownWrong = ["Bleach.S16E19", "[TESHI]Bleach-236", "Bleach - 329 [SGKK]"];
        string[] foreign =
        [
            "Naruto Shippuden Thousand-Year Blood War S17E10 1080p",
            "Attack on Titan TYBW S17E20 1080p",
            "Thousand-Year Blood War S17E10 1080p",
        ];

        string[] titles = [.. knownWrong, .. foreign, toonsHub, sennenKessenHen];
        var result = ReleaseRanking.Rank(titles, new RankingContext(Bleach, [Bleach], BleachArcMap));

        Assert.Equal(titles.Length, result.Ranked.Count);
        Assert.Empty(result.Degradations);

        foreach (var title in new[] { toonsHub, sennenKessenHen })
        {
            var entry = Assert.Single(result.Ranked, r => r.Release.Title == title);
            Assert.True(entry.MeetsAcceptanceThreshold, $"{title} scored {entry.Confidence:F3}");
            Assert.Equal(ReleaseSeriesRelation.Same, entry.Release.Relation);
            Assert.Equal(17, entry.BestNumbering!.Candidate.Season);
            Assert.Equal(402, entry.BestNumbering.Candidate.Absolute);
        }

        Assert.Contains(result.Ranked[0].Release.Title, new[] { toonsHub, sennenKessenHen });
        Assert.Contains(result.Ranked[1].Release.Title, new[] { toonsHub, sennenKessenHen });

        foreach (var title in knownWrong)
        {
            var entry = Assert.Single(result.Ranked, r => r.Release.Title == title);
            Assert.False(entry.MeetsAcceptanceThreshold, $"{title} reached {entry.Confidence:F3}");
        }

        foreach (var title in foreign)
        {
            var entry = Assert.Single(result.Ranked, r => r.Release.Title == title);
            Assert.Equal(ReleaseSeriesRelation.Unknown, entry.Release.Relation);
            Assert.NotNull(entry.DeRankReason);
            Assert.False(entry.MeetsAcceptanceThreshold, $"{title} reached {entry.Confidence:F3}");
            // Withheld arc map: the borrowed arc words earn no corroboration for a foreign series.
            Assert.True(entry.Confidence < new ScoringWeights().UnknownSeriesPenalty, $"{title} reached {entry.Confidence:F3}");
        }
    }
}
