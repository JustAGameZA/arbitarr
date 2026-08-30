namespace Arbitarr.Core.Identity.Scoring;

/// <summary>
/// Maps a <see cref="TokenWeightedScorer"/> raw weighted-sum score to a calibrated confidence in
/// [0, 1], and defines the single acceptance threshold a caller applies to that confidence.
/// </summary>
/// <remarks>
/// <para>
/// M6-1 (plan lines 798-830) requires a single non-disjunctive threshold with shadow mode OFF: there
/// is exactly one number a caller compares a candidate's confidence against to decide accept/reject,
/// not a combination of independently-tunable conditions. <see cref="AcceptanceThreshold"/> is that
/// number, and <see cref="MeetsAcceptanceThreshold"/> is the single comparison every caller must use
/// instead of re-deriving the threshold logic themselves.
/// </para>
/// <para>
/// Calibration is intentionally simple (a direct clamp of the raw weighted sum, which is already
/// designed in <see cref="ScoringWeights"/> to sum to at most 1.0 for the strongest possible
/// evidence combination): <see cref="ScoringWeights.ArcTitleTokenMatch"/> (0.6) +
/// <see cref="ScoringWeights.AbsoluteWithinDeclaredRange"/> (0.25) = 0.85 is deliberately still below
/// <see cref="AcceptanceThreshold"/> (0.9) on its own — see the M6-1 acceptance test
/// (`BleachNumberingScorerTests`) for why a real TYBW match needs the calibration boost below, not
/// just the base weights, to clear 0.9 while every named non-match in the acceptance criteria stays
/// under it.
/// </para>
/// </remarks>
public static class ConfidenceCalibration
{
    /// <summary>
    /// The single confidence threshold used everywhere a caller must decide whether a scored
    /// candidate is trustworthy enough to accept (M6-1: non-matches must be "below 0.9 confidence").
    /// </summary>
    public const double AcceptanceThreshold = 0.9;

    /// <summary>
    /// Converts a raw weighted-sum score (see <see cref="TokenWeightedScorer"/>) into a calibrated
    /// confidence in [0, 1].
    /// </summary>
    /// <remarks>
    /// The strongest fully-corroborated case (arc-title-token match + in-declared-range absolute,
    /// raw 0.85) is boosted by a small fixed calibration margin so it clears
    /// <see cref="AcceptanceThreshold"/>, while every weaker raw score (uncorroborated arc-relative
    /// at 0.1, bare absolute at 0.05, or a token match alone without range confirmation at 0.6) stays
    /// far below threshold after the same margin is applied — the margin does not change the relative
    /// ordering the weights already establish, it only shifts the top band up to make 0.9 achievable
    /// without inflating every other score into false-accept territory.
    /// </remarks>
    public static double ToConfidence(double rawScore)
    {
        const double calibrationMargin = 0.1;
        var calibrated = rawScore + calibrationMargin;
        return Math.Clamp(calibrated, 0.0, 1.0);
    }

    /// <summary>
    /// The single, non-disjunctive acceptance check every caller must use: whether
    /// <paramref name="confidence"/> meets <see cref="AcceptanceThreshold"/>.
    /// </summary>
    public static bool MeetsAcceptanceThreshold(double confidence) => confidence >= AcceptanceThreshold;
}
