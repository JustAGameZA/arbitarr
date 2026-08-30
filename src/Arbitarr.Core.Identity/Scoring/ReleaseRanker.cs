namespace Arbitarr.Core.Identity.Scoring;

/// <summary>
/// How a release's resolved series relates to the requested one, as seen by
/// <see cref="ReleaseRanker"/>. Arbitarr.Media maps its franchise classification onto this so
/// Arbitarr.Core.Identity never names a Media type (M6-7 dependency direction).
/// </summary>
public enum ReleaseSeriesRelation
{
    /// <summary>Same series (shared TVDB/TMDB id or matching title family).</summary>
    Same,

    /// <summary>Same franchise, different series (e.g. SAC_2045 for a Stand Alone Complex query).</summary>
    Sibling,

    /// <summary>No franchise relation at all.</summary>
    Unrelated,

    /// <summary>Series could not be resolved from the title; no penalty is applied (fail-open).</summary>
    Unknown,
}

/// <summary>One release as the ranker sees it: already-built numbering evidence plus series relation.</summary>
/// <param name="Title">The raw release title, carried through for callers.</param>
/// <param name="NumberingEvidence">Per-candidate corroboration for the release's <see cref="CandidateNumberingSet"/>; empty when no numbering could be derived.</param>
/// <param name="Relation">Franchise relation of the release's resolved series to the requested one.</param>
/// <param name="RelationReason">Human-readable reason recorded when a relation penalty is applied.</param>
/// <param name="TitleSimilarity">0..1 similarity of the extracted series name to the requested identity.</param>
public sealed record RankableRelease(
    string Title,
    IReadOnlyList<NumberingCandidateCorroboration> NumberingEvidence,
    ReleaseSeriesRelation Relation,
    string? RelationReason,
    double TitleSimilarity);

/// <summary>A ranked release with its calibrated confidence and the threshold verdict.</summary>
public sealed record RankedRelease(
    RankableRelease Release,
    double Confidence,
    bool MeetsAcceptanceThreshold,
    NumberingCandidateScore? BestNumbering,
    string? DeRankReason);

/// <summary>Ordered ranking plus any degradation reasons recorded on the way (P3).</summary>
public sealed record RankingResult(IReadOnlyList<RankedRelease> Ranked, IReadOnlyList<string> Degradations);

/// <summary>
/// M6 step 5 ranking entry point. Orders releases by <see cref="ConfidenceCalibration.ToConfidence"/>
/// of their strongest evidence, applies the franchise penalties from <see cref="ScoringWeights"/>,
/// and reports <see cref="ConfidenceCalibration.MeetsAcceptanceThreshold"/> per entry against the
/// single non-disjunctive threshold. Names no Arbitarr.Media type; Media composes the input.
/// </summary>
public static class ReleaseRanker
{
    /// <summary>Degradation reason recorded when no release carried any numbering corroboration (P3).</summary>
    public const string SimilarityOnlyDegradation =
        "P3: no numbering corroboration available for any release; ranking is similarity-only";

    public static RankingResult Rank(
        IReadOnlyList<RankableRelease> releases,
        ScoringWeights? weights = null,
        IReadOnlyList<string>? degradations = null)
    {
        ArgumentNullException.ThrowIfNull(releases);

        var w = weights ?? new ScoringWeights();
        var recorded = new List<string>(degradations ?? []);

        if (releases.Count == 0)
        {
            return new RankingResult([], recorded);
        }

        var ranked = new List<(RankedRelease Entry, int Index)>(releases.Count);
        var anyCorroboration = false;

        for (var i = 0; i < releases.Count; i++)
        {
            var release = releases[i];
            anyCorroboration |= release.NumberingEvidence.Any(IsCorroborated);

            var bestNumbering = release.NumberingEvidence.Count > 0
                ? TokenWeightedScorer.ScoreBest(release.NumberingEvidence, w)
                : null;

            var similarityConfidence = ConfidenceCalibration.ToConfidence(
                Math.Clamp(release.TitleSimilarity, 0, 1) * w.SimilarityOnly);

            // Strongest available evidence wins: a corroborated numbering candidate outranks a bare
            // title match, but an uncorroborated numbering carry-through must not drag a strong
            // title match below the similarity-only floor.
            var baseConfidence = Math.Max(bestNumbering?.Confidence ?? 0, similarityConfidence);

            var (factor, reason) = release.Relation switch
            {
                ReleaseSeriesRelation.Sibling => (w.SiblingSeriesPenalty, release.RelationReason ?? "sibling series"),
                ReleaseSeriesRelation.Unrelated => (w.UnrelatedSeriesPenalty, release.RelationReason ?? "unrelated series"),
                _ => (1.0, (string?)null),
            };

            var confidence = Math.Clamp(baseConfidence * factor, 0, 1);

            ranked.Add((new RankedRelease(
                release,
                confidence,
                ConfidenceCalibration.MeetsAcceptanceThreshold(confidence),
                bestNumbering,
                factor < 1.0 ? reason : null), i));
        }

        if (!anyCorroboration)
        {
            recorded.Add(SimilarityOnlyDegradation);
        }

        var ordered = ranked
            .OrderByDescending(r => r.Entry.Confidence)
            .ThenBy(r => r.Index)
            .Select(r => r.Entry)
            .ToArray();

        return new RankingResult(ordered, recorded);
    }

    private static bool IsCorroborated(NumberingCandidateCorroboration c) =>
        c.ArcTitleTokenMatched || c.ArcSceneSeasonAliasMatched || c.AbsoluteWithinDeclaredRange == true;
}
