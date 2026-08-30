namespace Arbitarr.Core.Identity.Scoring;

/// <summary>
/// Per-candidate corroboration facts the scorer needs but that <see cref="NumberingCandidate"/>
/// itself does not carry (arc-title-token match, declared-range membership). Deliberately named and
/// shaped so nothing in <c>Arbitarr.Media</c> needs to be referenced by name here — the caller
/// (composition code in Arbitarr.Media/Host, per plan step 2) computes these booleans from its own
/// <c>ArcSeasonBinding</c>/<c>ArcSeasonMap</c> data and passes them across as plain data.
/// </summary>
/// <param name="Candidate">The candidate being scored.</param>
/// <param name="ArcTitleTokenMatched">
/// Whether this candidate's arc binding (if any) was resolved via an exact arc-title-token match
/// against the release's own title (docs row: `Bleach S17E42 ... SON OF DARKNESS` via
/// <c>ArcTitleToken="Thousand-Year Blood War"</c>).
/// </param>
/// <param name="ArcSceneSeasonAliasMatched">
/// Whether this candidate's arc binding was instead resolved via the scene-season-alias path (step
/// 3's <c>ArcSeasonBinding.AlternateSceneSeasons</c>, docs row: `BLEACH Sennen Kessen hen
/// S01E36...` - a release whose own arc-title token doesn't match the binding's
/// <c>AlternateArcTitles</c> at all, but whose scene season is a known alternate rendering of the
/// same arc). The release's own title didn't name the arc in a way this data recognizes, but a real
/// binding (not a guess) was still found, so this scores at the same tier as a title-token match
/// (see <see cref="ScoringWeights.ArcSceneSeasonAliasMatch"/>). Mutually exclusive with
/// <see cref="ArcTitleTokenMatched"/> in practice (title-token match is tried first).
/// </param>
/// <param name="AbsoluteWithinDeclaredRange">
/// Whether <see cref="NumberingCandidate.Absolute"/> falls within the resolved arc binding's own
/// declared absolute range. Null when there is no arc binding or no absolute to check (see
/// <see cref="ScoringWeights.AbsoluteWithinDeclaredRange"/> for why this must be withheld, not
/// scored as false, in that case).
/// </param>
public sealed record NumberingCandidateCorroboration(
    NumberingCandidate Candidate,
    bool ArcTitleTokenMatched,
    bool ArcSceneSeasonAliasMatched,
    bool? AbsoluteWithinDeclaredRange);

/// <summary>One candidate's computed score, kept alongside the candidate it was computed for.</summary>
/// <param name="Candidate">The scored candidate.</param>
/// <param name="Confidence">Calibrated confidence in [0, 1], via <see cref="ConfidenceCalibration"/>.</param>
public sealed record NumberingCandidateScore(NumberingCandidate Candidate, double Confidence);

/// <summary>
/// Scores an already-built <see cref="CandidateNumberingSet"/>'s candidates against corroborating
/// facts about how each candidate's arc binding (if any) was resolved, per <see cref="ScoringWeights"/>.
/// </summary>
/// <remarks>
/// <para>
/// Step 2 of plan lines 798-830: this type takes only already-built data (a
/// <see cref="CandidateNumberingSet"/> plus <see cref="NumberingCandidateCorroboration"/> facts) — it
/// does not itself resolve arc bindings, parse titles, or reference any <c>Arbitarr.Media</c> type by
/// name. Composition (building the corroboration facts from an <c>ArcSeasonMap</c> and calling this
/// scorer) lives in <c>Arbitarr.Media</c>, so <c>Arbitarr.Core.Identity.csproj</c> takes no new
/// project reference (AC6a).
/// </para>
/// <para>
/// R5 (plan lines 798-830): a bare <c>(Season: 1, ...)</c> <see cref="NumberingScheme.ArcRelative"/>
/// candidate must never be scored at all, even defensively — <see cref="CandidateNumberingSetBuilder"/>
/// (Media) already guarantees one is never generated (AC-M1), but this scorer treats that guarantee
/// as untrusted input and re-excludes the shape itself rather than relying solely on the upstream
/// contract. Likewise, a bare <see cref="NumberingScheme.Absolute"/> candidate is never allowed to
/// "key" a match on its own: <see cref="ScoreAll"/> always includes it (so it can still rank, at its
/// low <see cref="ScoringWeights.BareAbsoluteOnly"/> weight, when nothing else exists), but
/// <see cref="ScoreBest"/> - the ranking entry point - refuses to return a bare-absolute candidate as
/// the winning best-scheme pick unless no non-absolute candidate exists at all.
/// </para>
/// </remarks>
public static class TokenWeightedScorer
{
    /// <summary>
    /// Scores every candidate in <paramref name="corroborations"/> against <paramref name="weights"/>,
    /// excluding (never emitting a score for) any bare <c>(Season: 1, ...)</c> ArcRelative candidate.
    /// </summary>
    public static IReadOnlyList<NumberingCandidateScore> ScoreAll(
        IReadOnlyList<NumberingCandidateCorroboration> corroborations,
        ScoringWeights weights)
    {
        ArgumentNullException.ThrowIfNull(corroborations);
        ArgumentNullException.ThrowIfNull(weights);

        var scores = new List<NumberingCandidateScore>();

        foreach (var corroboration in corroborations)
        {
            var candidate = corroboration.Candidate;

            // R5: never score a bare (season=1, arc-relative) candidate, regardless of what the
            // builder upstream has already guaranteed (AC-M1). This is a defensive re-check, not a
            // primary enforcement point.
            if (candidate.Scheme == NumberingScheme.ArcRelative && candidate.Season == 1)
            {
                continue;
            }

            var raw = ComputeRawScore(corroboration, weights);
            var confidence = ConfidenceCalibration.ToConfidence(raw);
            scores.Add(new NumberingCandidateScore(candidate, confidence));
        }

        return scores;
    }

    /// <summary>
    /// Picks the single best-scoring candidate from <paramref name="corroborations"/>, or
    /// <see langword="null"/> if none score above zero. Never returns a bare
    /// <see cref="NumberingScheme.Absolute"/> candidate as the winner while any non-absolute
    /// candidate is present in the same set (R5's "never key on bare absolute" clause) — an
    /// absolute-only candidate may only win when it is the sole candidate available.
    /// </summary>
    public static NumberingCandidateScore? ScoreBest(
        IReadOnlyList<NumberingCandidateCorroboration> corroborations,
        ScoringWeights weights)
    {
        var scores = ScoreAll(corroborations, weights);
        if (scores.Count == 0)
        {
            return null;
        }

        var nonAbsolute = scores.Where(s => s.Candidate.Scheme != NumberingScheme.Absolute).ToArray();
        var pool = nonAbsolute.Length > 0 ? nonAbsolute : scores;

        return pool.OrderByDescending(s => s.Confidence).First();
    }

    private static double ComputeRawScore(NumberingCandidateCorroboration corroboration, ScoringWeights weights)
    {
        var candidate = corroboration.Candidate;

        if (candidate.Scheme == NumberingScheme.Absolute)
        {
            // Bare absolute standing alone: small fixed weight, never boosted by corroboration
            // fields (those describe arc-binding resolution, which an Absolute-scheme candidate by
            // definition has none of).
            return weights.BareAbsoluteOnly;
        }

        var score = 0.0;

        if (corroboration.ArcTitleTokenMatched)
        {
            score += weights.ArcTitleTokenMatch;
        }
        else if (corroboration.ArcSceneSeasonAliasMatched)
        {
            // Scene-season-alias resolution (docs row: `BLEACH Sennen Kessen hen S01E36...`): a
            // real binding was found, just not via the release's own arc-title vocabulary.
            score += weights.ArcSceneSeasonAliasMatch;
        }
        else if (candidate.Scheme == NumberingScheme.ArcRelative)
        {
            // Uncorroborated arc-relative carry-through (docs: bare `Bleach S17E##...` titles).
            return weights.UncorroboratedArcRelative;
        }
        else
        {
            return score;
        }

        // Range corroboration only means anything once a binding was actually resolved.
        // AbsoluteWithinDeclaredRange == false (out-of-range, e.g. the E42/E45 overshoot rows) or
        // null (nothing to check) both simply withhold the bonus - neither is penalized below the
        // plain binding-match weight, since the binding match itself is still true.
        if (corroboration.AbsoluteWithinDeclaredRange == true)
        {
            score += weights.AbsoluteWithinDeclaredRange;
        }

        return score;
    }
}
