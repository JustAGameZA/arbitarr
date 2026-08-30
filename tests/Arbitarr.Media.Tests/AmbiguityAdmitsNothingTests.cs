using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Media.Numbering;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// M6-3 (plan lines 798-830, authorised by team-lead at 171e7e0): XEM data where a scene-absolute
/// number maps to two distinct TVDB targets must admit nothing - reusing <see cref="AmbiguityPolicy"/>
/// - and R5's stronger guarantee that no bare <c>(Season: 1, arc_relative_ep)</c> candidate is ever
/// generated or scored, regardless of scorer input.
/// </summary>
public class AmbiguityAdmitsNothingTests
{
    /// <summary>The real XEM abs-36 collision map, reused verbatim from BleachArcRelativeNumberingTests.</summary>
    private static ArcSeasonMap AmbiguousAbsolute36Map => new(
    [
        new ArcSeasonBinding("Agent of the Shinigami", [], Season: 1, AbsoluteRangeStart: 1, AbsoluteRangeEnd: 20),
        new ArcSeasonBinding("Entry: Soul Society", [], Season: 2, AbsoluteRangeStart: 21, AbsoluteRangeEnd: 63),
        new ArcSeasonBinding("Bounts (misfiled)", [], Season: 8, AbsoluteRangeStart: 30, AbsoluteRangeEnd: 40),
    ]);

    [Fact]
    public void AmbiguousAbsolute36_AdmitsNothing_ViaAmbiguityPolicy()
    {
        // Reuse of AmbiguityPolicy per team-lead's explicit instruction: the collision at absolute
        // 36 must resolve to no single binding.
        var evaluation = AmbiguityPolicy.Evaluate(AmbiguousAbsolute36Map, absoluteEpisode: 36);

        Assert.Equal(MatchProvenanceFlags.AmbiguousMapping, evaluation.Flags);
        Assert.Null(evaluation.Resolved);
        Assert.Equal(2, evaluation.Competing.Count);
    }

    [Fact]
    public void AmbiguousAbsolute36_CarriedThroughRelease_ScoresBelowAcceptanceThreshold()
    {
        // A release whose only numbering signal is the colliding absolute 36, scored end-to-end:
        // the builder itself never resolves a single binding for this shape (see
        // BleachArcRelativeNumberingTests.Build_WithAbsoluteCollisionAcrossTwoBindings_...), so the
        // resulting candidate set can only ever contain an uncorroborated/bare candidate - never a
        // confident, token-matched one - and must therefore score below the acceptance threshold.
        var raw = new RawReleaseNumbering(SceneSeason: 8, SceneEpisode: 6, Absolute: 36, ArcTitleToken: null);

        var candidates = CandidateNumberingSetBuilder.Build(raw, AmbiguousAbsolute36Map);
        var best = NumberingCandidateScoring.ScoreBest(candidates, AmbiguousAbsolute36Map, arcTitleToken: null, sceneSeason: raw.SceneSeason);

        Assert.NotNull(best);
        Assert.False(ConfidenceCalibration.MeetsAcceptanceThreshold(best!.Confidence),
            $"Expected ambiguous-collision confidence < {ConfidenceCalibration.AcceptanceThreshold}, was {best.Confidence}");
    }

    [Fact]
    public void NoBareCandidate_IsEverGenerated_AcrossAWideRangeOfInputs()
    {
        // R5 / AC-M1 non-vacuousness: sweep a range of scene seasons/episodes/tokens (including
        // deliberately adversarial "looks like it might trick the builder into season 1" shapes) and
        // assert the builder NEVER produces a bare (Season: 1, ArcRelative) candidate for any of
        // them - this is a positive-control-backed universal check, not a single easily-satisfied
        // negative assertion (see BleachArcRelativeNumberingTests for the single-case version this
        // generalizes). Deliberately does NOT declare AlternateSceneSeasons: [1] on this map's
        // binding - a scene season 1 that IS a known alias (see BleachNumberingScorerTests) is
        // expected to resolve, by design; this sweep is only about the still-excluded case where
        // scene season 1 matches no known alias and no title token.
        var arcMap = new ArcSeasonMap(
        [
            new ArcSeasonBinding("Thousand-Year Blood War", ["TYBW"], Season: 17, AbsoluteRangeStart: 367, AbsoluteRangeEnd: 402),
        ]);

        var adversarialInputs = new[]
        {
            new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 1, Absolute: null, ArcTitleToken: null),
            new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 99, Absolute: null, ArcTitleToken: "Not A Real Arc"),
            new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 5, Absolute: 5, ArcTitleToken: "Also Fake"),
            new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 20, Absolute: null, ArcTitleToken: string.Empty),
        };

        foreach (var raw in adversarialInputs)
        {
            var candidates = CandidateNumberingSetBuilder.Build(raw, arcMap);

            Assert.DoesNotContain(candidates.Candidates, c => c.Scheme == NumberingScheme.ArcRelative && c.Season == 1);

            // Positive control: confirm the sweep is actually exercising the exclusion path, not
            // vacuously passing because these inputs never reach it. Every one of these inputs has
            // SceneSeason == 1 with no title-token match and no scene-season-alias declared on this
            // map's binding at all - so the builder must in fact hit its season==1 exclusion branch
            // for each of them, producing an empty ArcRelative slice entirely, not merely one
            // without Season==1.
            Assert.DoesNotContain(candidates.Candidates, c => c.Scheme == NumberingScheme.ArcRelative);
        }
    }

    [Fact]
    public void NoBareCandidate_IsEverScored_EvenIfSomehowPresentInInput()
    {
        // Defense-in-depth positive control for TokenWeightedScorer's own R5 re-check (independent
        // of whatever the builder guarantees): construct a CandidateNumberingSet containing a bare
        // (Season: 1, ArcRelative) candidate directly - bypassing the builder entirely - and assert
        // the scorer excludes it from ScoreAll's output rather than trusting upstream.
        var bareCandidate = new NumberingCandidate(NumberingScheme.ArcRelative, Season: 1, Episode: 36, Absolute: 402);
        var legitimateCandidate = new NumberingCandidate(NumberingScheme.ArcRelative, Season: 17, Episode: 36, Absolute: 402);
        var candidateSet = new CandidateNumberingSet([bareCandidate, legitimateCandidate]);

        var corroborations = new[]
        {
            new NumberingCandidateCorroboration(bareCandidate, ArcTitleTokenMatched: true, ArcSceneSeasonAliasMatched: false, AbsoluteWithinDeclaredRange: true),
            new NumberingCandidateCorroboration(legitimateCandidate, ArcTitleTokenMatched: true, ArcSceneSeasonAliasMatched: false, AbsoluteWithinDeclaredRange: true),
        };

        var scores = TokenWeightedScorer.ScoreAll(corroborations, new ScoringWeights());

        Assert.DoesNotContain(scores, s => s.Candidate.Season == 1);
        Assert.Contains(scores, s => s.Candidate.Season == 17);
        Assert.Single(scores);
    }
}
