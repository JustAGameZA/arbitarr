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
/// M6 step 5 composition: resolves each release's series against the known
/// <see cref="SeriesIdentity"/> set FIRST, builds its <see cref="CandidateNumberingSet"/> against
/// the requested series' arc map only when it resolved to that series, feeds the
/// <see cref="FranchiseClassifier"/> verdict into the sibling/unrelated penalty, and hands the
/// result to <see cref="ReleaseRanker"/>. Sibling classification only de-ranks; nothing is dropped.
/// </summary>
/// <remarks>
/// Identity is a precondition for numbering, not a parallel signal. An arc title such as
/// "Thousand-Year Blood War" is only meaningful for the series whose arc map declares it; a
/// foreign release that happens to carry the same words (or a scene season that happens to be a
/// declared alias) must never earn corroboration credit from the requested series' map. So the
/// arc map is withheld entirely for any release that did not resolve to the requested series, and
/// the arc-title token is stripped from the release's series name before resolution so the arc
/// words themselves cannot pull a foreign series onto the requested identity via a
/// franchise-style alternate title (e.g. "Bleach: Thousand-Year Blood War").
/// </remarks>
public static class ReleaseRanking
{
    /// <summary>Minimum series-name similarity for a release to resolve to a known identity at all.</summary>
    private const double MinResolutionSimilarity = 0.5;

    /// <summary>
    /// Minimum lead the best-matching identity must hold over the runner-up. A release whose series
    /// name sits between two known identities (a tie, or near enough) is <see cref="ReleaseSeriesRelation.Unknown"/>
    /// rather than silently awarded to whichever identity happened to be enumerated first.
    /// </summary>
    private const double MinResolutionMargin = 0.05;

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
        var raw = RawReleaseNumberingParser.Parse(title, context.ArcMap);
        var seriesName = SeriesNameWithoutArcTitle(title, raw.ArcTitleToken);

        var (relation, reason) = Resolve(seriesName, context);

        // Numbering can only be corroborated against the requested series' own arc map, and only
        // for a release that is actually that series. Everything else keeps its bare parsed
        // numbering (uncorroborated carry-through) and is ranked on title similarity plus the
        // relation penalty alone.
        var arcMap = relation == ReleaseSeriesRelation.Same ? context.ArcMap : null;
        if (arcMap is null)
        {
            raw = raw with { ArcTitleToken = null };
        }

        var candidates = CandidateNumberingSetBuilder.Build(raw, arcMap);
        var evidence = NumberingCandidateScoring.BuildCorroborations(candidates, arcMap, raw.ArcTitleToken, raw.SceneSeason);

        return new RankableRelease(
            title,
            evidence,
            relation,
            reason,
            TitleSimilarity.Score(seriesName, context.Requested));
    }

    /// <summary>
    /// The release's series-name portion with the matched arc title removed. Arc words are
    /// evidence about the arc, not the series: leaving them in lets a foreign release share tokens
    /// with an arc-bearing alternate title of the requested series. A title that consists of
    /// nothing but the arc name has no series evidence at all and yields an empty name.
    /// </summary>
    private static string SeriesNameWithoutArcTitle(string title, string? arcTitleToken)
    {
        var seriesName = SeriesNameExtractor.Extract(title) ?? title;
        if (arcTitleToken is null)
        {
            return seriesName;
        }

        return seriesName
            .Replace(arcTitleToken, " ", StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', ':');
    }

    private static (ReleaseSeriesRelation Relation, string? Reason) Resolve(string seriesName, RankingContext context)
    {
        SeriesIdentity? resolved = null;
        SeriesIdentity? runnerUp = null;
        var bestSimilarity = 0.0;
        var runnerUpSimilarity = 0.0;

        foreach (var identity in context.KnownIdentities)
        {
            var similarity = TitleSimilarity.Score(seriesName, identity);
            if (similarity > bestSimilarity)
            {
                runnerUp = resolved;
                runnerUpSimilarity = bestSimilarity;
                resolved = identity;
                bestSimilarity = similarity;
            }
            else if (similarity > runnerUpSimilarity)
            {
                runnerUp = identity;
                runnerUpSimilarity = similarity;
            }
        }

        if (resolved is null || bestSimilarity < MinResolutionSimilarity)
        {
            return (ReleaseSeriesRelation.Unknown, "unknown series: title matches no known identity");
        }

        if (bestSimilarity - runnerUpSimilarity < MinResolutionMargin)
        {
            return (ReleaseSeriesRelation.Unknown, $"unknown series: ambiguous between '{resolved.PrimaryTitle}' and '{runnerUp!.PrimaryTitle}'");
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
