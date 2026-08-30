using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;

namespace Arbitarr.Media.Numbering;

/// <summary>
/// Composition point (plan step 2): builds <see cref="NumberingCandidateCorroboration"/> facts from
/// a <see cref="CandidateNumberingSet"/> plus the <see cref="ArcSeasonMap"/> and release-title
/// arc-title token used to build it, then hands them to <see cref="TokenWeightedScorer"/>.
/// </summary>
/// <remarks>
/// This is deliberately the only place that knows about both <c>Arbitarr.Media</c>'s
/// <see cref="ArcSeasonMap"/>/<see cref="ArcSeasonBinding"/> types and
/// <c>Arbitarr.Core.Identity.Scoring</c>'s scorer types — the scorer itself never references
/// <see cref="ArcSeasonMap"/> by name, per team-lead's layering instruction.
/// </remarks>
public static class NumberingCandidateScoring
{
    /// <summary>
    /// Scores every candidate in <paramref name="candidates"/>, recomputing per-candidate
    /// corroboration facts (arc-title-token match, declared-range membership) from
    /// <paramref name="arcMap"/> and <paramref name="arcTitleToken"/>.
    /// </summary>
    /// <param name="candidates">The already-built candidate set (see <see cref="CandidateNumberingSetBuilder"/>).</param>
    /// <param name="arcMap">The series' season-keyed arc-title map, or <see langword="null"/> if none is available.</param>
    /// <param name="arcTitleToken">
    /// The release's own arc-title token (see <see cref="RawReleaseNumbering.ArcTitleToken"/>), used
    /// to recompute whether a given candidate's season actually corresponds to a title-token match
    /// against <paramref name="arcMap"/>, as opposed to a scene-season-alias or absolute-lookup
    /// resolution.
    /// </param>
    /// <param name="weights">Scoring weights to apply. Defaults to <see cref="ScoringWeights"/>'s defaults when omitted.</param>
    public static IReadOnlyList<NumberingCandidateScore> ScoreAll(
        CandidateNumberingSet candidates,
        ArcSeasonMap? arcMap,
        string? arcTitleToken,
        ScoringWeights? weights = null,
        int? sceneSeason = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var corroborations = BuildCorroborations(candidates, arcMap, arcTitleToken, sceneSeason);
        return TokenWeightedScorer.ScoreAll(corroborations, weights ?? new ScoringWeights());
    }

    /// <summary>Picks the single best-scoring candidate. See <see cref="TokenWeightedScorer.ScoreBest"/>.</summary>
    public static NumberingCandidateScore? ScoreBest(
        CandidateNumberingSet candidates,
        ArcSeasonMap? arcMap,
        string? arcTitleToken,
        ScoringWeights? weights = null,
        int? sceneSeason = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var corroborations = BuildCorroborations(candidates, arcMap, arcTitleToken, sceneSeason);
        return TokenWeightedScorer.ScoreBest(corroborations, weights ?? new ScoringWeights());
    }

    public static IReadOnlyList<NumberingCandidateCorroboration> BuildCorroborations(
        CandidateNumberingSet candidates,
        ArcSeasonMap? arcMap,
        string? arcTitleToken,
        int? sceneSeason)
    {
        // Whether this release's own arc-title token is an exact title-token match against some
        // binding in the map at all (independent of which candidate we're annotating - a release
        // either does or doesn't carry a matching arc name in its own title).
        var tokenMatchedBinding = arcMap is not null && arcTitleToken is not null
            ? arcMap.FindByTitleToken(arcTitleToken)
            : null;

        // Whether this release's own scene season is an unambiguous scene-season alias for some
        // binding in the map (docs row: `BLEACH Sennen Kessen hen S01E36...` - scene season 1 is a
        // declared AlternateSceneSeasons entry for the TYBW binding even though the release's own
        // arc-title token matches nothing in AlternateArcTitles). Uses the same alias-only,
        // ambiguity-rejecting lookup the builder resolves through, so a candidate is only ever
        // credited as alias-matched when it could actually have been built that way - never for a
        // bare carry-through candidate whose scene season happens to equal some binding's own Season.
        var aliasMatchedBinding = arcMap is not null && sceneSeason is { } season
            ? arcMap.FindBySceneSeasonAlias(season)
            : null;

        var result = new List<NumberingCandidateCorroboration>(candidates.Candidates.Count);

        foreach (var candidate in candidates.Candidates)
        {
            if (candidate.Scheme != NumberingScheme.ArcRelative)
            {
                result.Add(new NumberingCandidateCorroboration(candidate, ArcTitleTokenMatched: false, ArcSceneSeasonAliasMatched: false, AbsoluteWithinDeclaredRange: null));
                continue;
            }

            // A candidate counts as arc-title-token-matched only when the token-matched binding's
            // own season is the season this candidate actually carries - guards against a set that
            // (hypothetically) contains more than one ArcRelative candidate under different
            // resolution paths.
            var tokenMatched = tokenMatchedBinding is not null && tokenMatchedBinding.Season == candidate.Season;

            // Same guard for the alias path; only credited when the title-token path didn't already
            // match, mirroring the builder's own try-title-token-first ordering.
            var aliasMatched = !tokenMatched && aliasMatchedBinding is not null && aliasMatchedBinding.Season == candidate.Season;

            var resolvedBinding = tokenMatched ? tokenMatchedBinding : (aliasMatched ? aliasMatchedBinding : null);

            bool? withinRange = null;
            if (resolvedBinding is not null && candidate.Absolute is { } absolute)
            {
                withinRange = resolvedBinding.CoversAbsolute(absolute);
            }

            result.Add(new NumberingCandidateCorroboration(candidate, tokenMatched, aliasMatched, withinRange));
        }

        return result;
    }
}
