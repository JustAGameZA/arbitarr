using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Core.Identity.Titles;
using Arbitarr.Media.Identity;
using Arbitarr.Media.Numbering;

namespace Arbitarr.Media.Ranking;

/// <summary>
/// Everything the composition needs to rank a page of release titles for one requested series.
/// </summary>
/// <param name="Requested">The series the caller asked for.</param>
/// <param name="KnownIdentities">Canonical identities a release may resolve to (the requested one plus its franchise neighbours); each stays a distinct entry and is never merged.</param>
/// <param name="ArcMap">Arc/season bindings for the requested series, or <c>null</c> when XEM / Anime-Lists were unreachable or the series has no arcs.</param>
/// <param name="Degradations">Upstream degradation reasons to carry onto the result (P3), e.g. which metadata source was unreachable.</param>
/// <param name="Weights">Scoring weights; defaults when <c>null</c>.</param>
public sealed record RankingContext(
    SeriesIdentity Requested,
    IReadOnlyList<SeriesIdentity> KnownIdentities,
    ArcSeasonMap? ArcMap,
    IReadOnlyList<string>? Degradations = null,
    ScoringWeights? Weights = null);

/// <summary>
/// M6 step 5 composition: builds each release's <see cref="CandidateNumberingSet"/>, resolves its
/// series against the known <see cref="SeriesIdentity"/> set, feeds the
/// <see cref="FranchiseClassifier"/> verdict into the sibling/unrelated penalty, and hands the
/// result to <see cref="ReleaseRanker"/>. Sibling classification only de-ranks; nothing is dropped.
/// </summary>
public static class ReleaseRanking
{
    /// <summary>Minimum title similarity for a release to resolve to a known identity at all.</summary>
    private const double MinResolutionSimilarity = 0.5;

    public static RankingResult Rank(IReadOnlyList<string> releaseTitles, RankingContext context)
    {
        ArgumentNullException.ThrowIfNull(releaseTitles);
        ArgumentNullException.ThrowIfNull(context);

        var rankable = new List<RankableRelease>(releaseTitles.Count);
        foreach (var title in releaseTitles)
        {
            rankable.Add(ToRankable(title, context));
        }

        return ReleaseRanker.Rank(rankable, context.Weights, context.Degradations);
    }

    private static RankableRelease ToRankable(string title, RankingContext context)
    {
        var seriesName = SeriesNameExtractor.Extract(title) ?? title;

        var raw = RawReleaseNumberingParser.Parse(title, context.ArcMap);
        var candidates = CandidateNumberingSetBuilder.Build(raw, context.ArcMap);
        var evidence = NumberingCandidateScoring.BuildCorroborations(candidates, context.ArcMap, raw.ArcTitleToken, raw.SceneSeason);

        var (relation, reason) = Resolve(seriesName, context);

        return new RankableRelease(
            title,
            evidence,
            relation,
            reason,
            TitleSimilarity.Score(seriesName, context.Requested));
    }

    private static (ReleaseSeriesRelation Relation, string? Reason) Resolve(string seriesName, RankingContext context)
    {
        SeriesIdentity? resolved = null;
        var bestSimilarity = 0.0;

        foreach (var identity in context.KnownIdentities)
        {
            var similarity = TitleSimilarity.Score(seriesName, identity);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                resolved = identity;
            }
        }

        if (resolved is null || bestSimilarity < MinResolutionSimilarity)
        {
            return (ReleaseSeriesRelation.Unknown, null);
        }

        var classification = FranchiseClassifier.Classify(context.Requested, resolved);
        var relation = classification.Relation switch
        {
            FranchiseRelation.Same => ReleaseSeriesRelation.Same,
            FranchiseRelation.Sibling => ReleaseSeriesRelation.Sibling,
            _ => ReleaseSeriesRelation.Unrelated,
        };

        return (relation, classification.Reason);
    }
}
