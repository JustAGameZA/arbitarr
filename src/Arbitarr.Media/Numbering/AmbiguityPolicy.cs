using Arbitarr.Core.Identity;

namespace Arbitarr.Media.Numbering;

/// <summary>
/// Decides whether an absolute-episode lookup against an <see cref="ArcSeasonMap"/> is safely
/// resolvable, or must be reported as ambiguous rather than guessed.
/// </summary>
/// <remarks>
/// <para>
/// Directly implements AC-M3a's required outcome for the Bleach flagship example: XEM's own
/// season-keyed names map contains a real collision at absolute episode 36 (two distinct arc bindings
/// both claim it). Given a lookup key that matches multiple rows, this policy admits none of the
/// competing candidates and reports <see cref="MatchProvenanceFlags.AmbiguousMapping"/> — it never
/// picks "the first one" or "the higher-confidence one", because there is no principled basis for
/// preferring one arc binding's absolute-range claim over another's when both come from the same
/// hand-edited, unversioned source (see plan R7).
/// </para>
/// </remarks>
public static class AmbiguityPolicy
{
    /// <summary>
    /// Evaluates an absolute-episode lookup against a series' arc map.
    /// </summary>
    /// <param name="arcMap">The series' season-keyed arc-title map, or <see langword="null"/> if none
    /// is available.</param>
    /// <param name="absoluteEpisode">The absolute episode number being looked up.</param>
    /// <returns>
    /// The evaluation outcome: exactly one binding (safe to use), zero bindings (no coverage), or two
    /// or more bindings (ambiguous — admit none).
    /// </returns>
    public static AmbiguityEvaluation Evaluate(ArcSeasonMap? arcMap, int absoluteEpisode)
    {
        if (arcMap is null || arcMap.Bindings.Count == 0)
        {
            return AmbiguityEvaluation.NoCoverage();
        }

        var matches = arcMap.FindByAbsolute(absoluteEpisode);

        return matches.Count switch
        {
            0 => AmbiguityEvaluation.NoCoverage(),
            1 => AmbiguityEvaluation.Safe(matches[0]),
            _ => AmbiguityEvaluation.Ambiguous(matches),
        };
    }
}

/// <summary>
/// The outcome of <see cref="AmbiguityPolicy.Evaluate"/>: either a single safely-resolved binding, or
/// one of the two degraded/ambiguous conditions that must be independently distinguishable per AC-M6
/// (no coverage) and AC-M3a (ambiguous mapping).
/// </summary>
public sealed record AmbiguityEvaluation
{
    private AmbiguityEvaluation(
        MatchProvenanceFlags flags,
        ArcSeasonBinding? resolved,
        IReadOnlyList<ArcSeasonBinding> competing)
    {
        Flags = flags;
        Resolved = resolved;
        Competing = competing;
    }

    /// <summary>Degradation/ambiguity flags describing this outcome.</summary>
    public MatchProvenanceFlags Flags { get; }

    /// <summary>The single resolved binding, when <see cref="Flags"/> is <see cref="MatchProvenanceFlags.None"/>.</summary>
    public ArcSeasonBinding? Resolved { get; }

    /// <summary>
    /// The competing bindings that could not be disambiguated, when <see cref="Flags"/> has
    /// <see cref="MatchProvenanceFlags.AmbiguousMapping"/> set. Empty otherwise.
    /// </summary>
    public IReadOnlyList<ArcSeasonBinding> Competing { get; }

    public static AmbiguityEvaluation Safe(ArcSeasonBinding binding) =>
        new(MatchProvenanceFlags.None, binding, Array.Empty<ArcSeasonBinding>());

    public static AmbiguityEvaluation NoCoverage() =>
        new(MatchProvenanceFlags.NoXemCoverage, null, Array.Empty<ArcSeasonBinding>());

    public static AmbiguityEvaluation Ambiguous(IReadOnlyList<ArcSeasonBinding> competing) =>
        new(MatchProvenanceFlags.AmbiguousMapping, null, competing);
}
