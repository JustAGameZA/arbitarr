using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Core.Identity.Titles;

namespace Arbitarr.Core.Identity.Tests;

/// <summary>
/// M6 step 5 (plan lines 791-863): <see cref="ReleaseRanker"/> orders releases by calibrated
/// confidence, exposes the single 0.9 threshold verdict per entry, and consumes every relation
/// penalty in <see cref="ScoringWeights"/>. It sees only Core.Identity types.
/// </summary>
public class ReleaseRankerTests
{
    private static NumberingCandidateCorroboration Corroborated(int season, int episode, int absolute, bool withinRange = true) =>
        new(new NumberingCandidate(NumberingScheme.ArcRelative, season, episode, absolute), ArcTitleTokenMatched: true, ArcSceneSeasonAliasMatched: false, AbsoluteWithinDeclaredRange: withinRange);

    private static NumberingCandidateCorroboration Uncorroborated(int season, int episode) =>
        new(new NumberingCandidate(NumberingScheme.ArcRelative, season, episode, null), false, false, null);

    private static RankableRelease Release(
        string title,
        IReadOnlyList<NumberingCandidateCorroboration>? evidence = null,
        ReleaseSeriesRelation relation = ReleaseSeriesRelation.Same,
        string? reason = null,
        double similarity = 1.0) =>
        new(title, evidence ?? [], relation, reason, similarity);

    [Fact]
    public void EmptyInput_YieldsEmptyRanking_AndCarriesUpstreamDegradations()
    {
        var result = ReleaseRanker.Rank([], degradations: ["XEM unreachable"]);

        Assert.Empty(result.Ranked);
        Assert.Equal(["XEM unreachable"], result.Degradations);
    }

    [Fact]
    public void Rank_OrdersByConfidenceDescending_ThenByInputOrder()
    {
        var result = ReleaseRanker.Rank(
        [
            Release("weak", [Uncorroborated(17, 3)], similarity: 0.0),
            Release("strong", [Corroborated(17, 3, 369)]),
            Release("similar-only", similarity: 1.0),
            Release("similar-only-second", similarity: 1.0),
        ]);

        Assert.Equal(["strong", "similar-only", "similar-only-second", "weak"], result.Ranked.Select(r => r.Release.Title));
        Assert.Equal(0.95, result.Ranked[0].Confidence, precision: 10);
        Assert.Equal(0.6, result.Ranked[1].Confidence, precision: 10);
        Assert.Equal(0.2, result.Ranked[3].Confidence, precision: 10);
    }

    [Fact]
    public void MeetsAcceptanceThreshold_MatchesConfidenceCalibration_PerEntry()
    {
        var result = ReleaseRanker.Rank(
        [
            Release("corroborated", [Corroborated(17, 3, 369)]),
            Release("overshoot", [Corroborated(17, 45, 411, withinRange: false)]),
            Release("similar-only"),
        ]);

        Assert.All(result.Ranked, r => Assert.Equal(ConfidenceCalibration.MeetsAcceptanceThreshold(r.Confidence), r.MeetsAcceptanceThreshold));
        Assert.True(result.Ranked.Single(r => r.Release.Title == "corroborated").MeetsAcceptanceThreshold);
        Assert.False(result.Ranked.Single(r => r.Release.Title == "overshoot").MeetsAcceptanceThreshold);
        Assert.False(result.Ranked.Single(r => r.Release.Title == "similar-only").MeetsAcceptanceThreshold);
    }

    [Fact]
    public void SimilarityAlone_CanNeverMeetThreshold()
    {
        var result = ReleaseRanker.Rank([Release("perfect title match", similarity: 1.0)]);

        var entry = Assert.Single(result.Ranked);
        Assert.Equal(0.6, entry.Confidence, precision: 10);
        Assert.False(entry.MeetsAcceptanceThreshold);
        Assert.Null(entry.BestNumbering);
    }

    [Fact]
    public void SiblingPenalty_IsConsumed_AndRecordsReason_WithoutDropping()
    {
        var weights = new ScoringWeights { SiblingSeriesPenalty = 0.4 };

        var result = ReleaseRanker.Rank(
        [
            Release("same", [Corroborated(17, 3, 369)]),
            Release("sibling", [Corroborated(17, 3, 369)], ReleaseSeriesRelation.Sibling, "sibling, not same series"),
        ], weights);

        Assert.Equal(2, result.Ranked.Count);
        Assert.Equal("same", result.Ranked[0].Release.Title);
        var sibling = result.Ranked[1];
        Assert.Equal(0.95 * 0.4, sibling.Confidence, precision: 10);
        Assert.Equal("sibling, not same series", sibling.DeRankReason);
        Assert.Null(result.Ranked[0].DeRankReason);
    }

    [Fact]
    public void RelationPenalties_AreConsumed_AndOrdered_SiblingAboveUnknownAboveUnrelated()
    {
        var result = ReleaseRanker.Rank(
        [
            Release("sibling", relation: ReleaseSeriesRelation.Sibling),
            Release("unrelated", relation: ReleaseSeriesRelation.Unrelated),
            Release("unknown", relation: ReleaseSeriesRelation.Unknown),
        ]);

        var w = new ScoringWeights();
        Assert.Equal(["sibling", "unknown", "unrelated"], result.Ranked.Select(r => r.Release.Title));
        Assert.Equal(0.6 * w.SiblingSeriesPenalty, result.Ranked[0].Confidence, precision: 10);
        Assert.Equal(0.6 * w.UnknownSeriesPenalty, result.Ranked[1].Confidence, precision: 10);
        Assert.Equal(0.6 * w.UnrelatedSeriesPenalty, result.Ranked[2].Confidence, precision: 10);
        Assert.Equal("sibling series", result.Ranked[0].DeRankReason);
        Assert.Equal("unknown series", result.Ranked[1].DeRankReason);
        Assert.Equal("unrelated series", result.Ranked[2].DeRankReason);
    }

    /// <summary>
    /// Identity is a precondition for acceptance: no relation other than Same can clear the
    /// threshold, even with the strongest possible numbering evidence and a perfect title match.
    /// The release is still ranked and returned (P1 fail-open), just never accepted.
    /// </summary>
    [Theory]
    [InlineData(ReleaseSeriesRelation.Unknown)]
    [InlineData(ReleaseSeriesRelation.Sibling)]
    [InlineData(ReleaseSeriesRelation.Unrelated)]
    public void NonSameRelation_CannotMeetThreshold_EvenWithFullyCorroboratedNumbering(ReleaseSeriesRelation relation)
    {
        var result = ReleaseRanker.Rank([Release("foreign", [Corroborated(17, 3, 369)], relation, reason: null, similarity: 1.0)]);

        var entry = Assert.Single(result.Ranked);
        Assert.False(entry.MeetsAcceptanceThreshold, $"{relation} reached {entry.Confidence:F3}");
        Assert.NotNull(entry.DeRankReason);
        Assert.NotNull(entry.BestNumbering);
    }

    [Fact]
    public void UnknownRelation_UsesRelationReason_WhenSupplied()
    {
        var result = ReleaseRanker.Rank([Release("x", relation: ReleaseSeriesRelation.Unknown, reason: "unknown series: ambiguous")]);

        Assert.Equal("unknown series: ambiguous", Assert.Single(result.Ranked).DeRankReason);
    }

    [Fact]
    public void NoCorroborationAnywhere_RecordsSimilarityOnlyDegradation()
    {
        var result = ReleaseRanker.Rank(
        [
            Release("a", [Uncorroborated(17, 1)]),
            Release("b"),
        ], degradations: ["Anime-Lists unreachable"]);

        Assert.Equal(["Anime-Lists unreachable", ReleaseRanker.SimilarityOnlyDegradation], result.Degradations);
    }

    [Fact]
    public void AnyCorroboration_DoesNotRecordSimilarityOnlyDegradation()
    {
        var result = ReleaseRanker.Rank([Release("a", [Corroborated(17, 1, 367)]), Release("b")]);

        Assert.Empty(result.Degradations);
    }

    [Fact]
    public void UncorroboratedNumbering_DoesNotDragStrongTitleMatchBelowSimilarityFloor()
    {
        var result = ReleaseRanker.Rank([Release("bare S16E19", [Uncorroborated(16, 19)], similarity: 1.0)]);

        Assert.Equal(0.6, Assert.Single(result.Ranked).Confidence, precision: 10);
    }

    [Fact]
    public void TitleSimilarity_IsJaccardOverTokens_AndTakesBestAlternate()
    {
        var identity = new SeriesIdentity(74796, null, "Bleach", ["BLEACH Sennen Kessen hen"]);

        Assert.Equal(1.0, TitleSimilarity.Score("Bleach", identity), precision: 10);
        Assert.Equal(1.0, TitleSimilarity.Score("bleach sennen kessen HEN", identity), precision: 10);
        Assert.Equal(0.0, TitleSimilarity.Score("", identity), precision: 10);
        Assert.Equal(0.5, TitleSimilarity.Score("Ghost in the Shell Arise", "Ghost in the Shell: Stand Alone Complex"), precision: 10);
    }
}
