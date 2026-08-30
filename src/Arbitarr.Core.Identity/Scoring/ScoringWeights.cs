namespace Arbitarr.Core.Identity.Scoring;

/// <summary>
/// Named weights <see cref="TokenWeightedScorer"/> applies when scoring a single
/// <see cref="NumberingCandidate"/> against a release title, plus the relation and similarity factors
/// <see cref="ReleaseRanker"/> applies per release. Each numbering weight below cites the
/// specific docs/step3b-observed-failures.md section 5 row it addresses.
/// </summary>
/// <remarks>
/// Values are deliberately expressed as a 0..1 contribution to a single weighted sum, not as
/// independent probabilities — <see cref="ConfidenceCalibration"/> is what turns the sum into a
/// calibrated confidence, so the weights only need to be consistently ordered relative to each
/// other (arc-title corroboration is worth more than an uncorroborated absolute derivation, which
/// is worth more than nothing).
/// </remarks>
public sealed record ScoringWeights
{
    /// <summary>
    /// Base weight for any <see cref="NumberingScheme.ArcRelative"/> candidate whose arc binding was
    /// resolved via an exact arc-title-token match (docs row: `Bleach S17E42 ... SON OF DARKNESS`
    /// resolves via `ArcTitleToken="Thousand-Year Blood War"` matching `TybwBinding.ArcTitle`
    /// verbatim). This is the strongest single signal available: the release's own title names the
    /// arc the candidate claims to belong to.
    /// </summary>
    public double ArcTitleTokenMatch { get; init; } = 0.6;

    /// <summary>
    /// Base weight for an <see cref="NumberingScheme.ArcRelative"/> candidate whose arc binding was
    /// resolved via the scene-season-alias path instead of a title-token match (docs row: `BLEACH
    /// Sennen Kessen hen S01E36...` - scene season 1 is a known <c>AlternateSceneSeasons</c> entry
    /// for the TYBW binding even though its own arc-title token, the Japanese "Sennen Kessen hen",
    /// matches nothing in <c>AlternateArcTitles</c>). Set equal to <see cref="ArcTitleTokenMatch"/>:
    /// both paths resolve to a real, specific arc binding rather than a guess, and M6-1's acceptance
    /// criteria requires this exact alias case to reach the same confidence tier as a title-token
    /// match once corroborated by <see cref="AbsoluteWithinDeclaredRange"/>.
    /// </summary>
    public double ArcSceneSeasonAliasMatch { get; init; } = 0.6;

    /// <summary>
    /// Additional weight when the candidate's derived absolute number falls within its own arc
    /// binding's declared <c>AbsoluteRangeStart</c>/<c>AbsoluteRangeEnd</c> (docs row: `Bleach
    /// S17E45 DEFEND YOU` and `Bleach S17E42 ... SON OF DARKNESS` both derive absolutes past the
    /// binding's declared range end (408, 405 > 402) — this is flagged in the doc as a
    /// "range-completeness gap", so a candidate landing inside the declared range is more
    /// trustworthy than one that overshoots it). Withheld entirely (not just reduced) when the
    /// candidate has no absolute to check, since "no data" must not score the same as "checked and
    /// consistent".
    /// </summary>
    public double AbsoluteWithinDeclaredRange { get; init; } = 0.25;

    /// <summary>
    /// Weight for a bare <see cref="NumberingScheme.ArcRelative"/> candidate with no arc-title-token
    /// corroboration at all — i.e. scene season/episode carried through as-is with no binding
    /// resolved (docs row: `Bleach S17E##...` bare titles with no arc words produce
    /// `ArcRelative(Season:17, Episode:N, Absolute:null)`, explicitly called out as "a weaker,
    /// unconfirmed" candidate relative to the token-matched ones). Small but non-zero: this is still
    /// scene-season information, just uncorroborated.
    /// </summary>
    public double UncorroboratedArcRelative { get; init; } = 0.1;

    /// <summary>
    /// Weight for a bare <see cref="NumberingScheme.Absolute"/> candidate standing entirely on its
    /// own, with no <see cref="NumberingScheme.ArcRelative"/> candidate in the same
    /// <see cref="CandidateNumberingSet"/> to corroborate it. Deliberately small: R5 requires the
    /// scorer never "key on bare absolute" alone (plan lines 798-830), so this weight exists only to
    /// let an absolute-only candidate rank below any arc-relative alternative, never to let it win a
    /// match by itself. See <see cref="TokenWeightedScorer"/>'s explicit bare-absolute floor.
    /// </summary>
    public double BareAbsoluteOnly { get; init; } = 0.05;

    /// <summary>
    /// Multiplicative factor (0..1) <see cref="ReleaseRanker"/> applies to a release whose resolved
    /// series is a franchise sibling of the requested one (M6-2 GitS de-ranking: a SAC_2045 release
    /// answering a 2002 Stand Alone Complex query). A de-rank, never a gate: the release stays in
    /// the ranked list, it just cannot outrank a same-series release with equal evidence.
    /// </summary>
    public double SiblingSeriesPenalty { get; init; } = 0.5;

    /// <summary>
    /// Multiplicative factor (0..1) <see cref="ReleaseRanker"/> applies to a release whose resolved
    /// series is unrelated to the requested one (no shared TVDB/TMDB id and no shared franchise lead
    /// phrase). Stronger than <see cref="SiblingSeriesPenalty"/>, still not a gate.
    /// </summary>
    public double UnrelatedSeriesPenalty { get; init; } = 0.25;

    /// <summary>
    /// Weight <see cref="ReleaseRanker"/> multiplies a release's 0..1 title similarity by when no
    /// numbering corroboration is available (P1 fail-open / P3 degraded path: XEM and Anime-Lists
    /// unreachable, or a series with no arc map at all). Capped below the acceptance threshold by
    /// construction: <c>ToConfidence(1.0 * 0.5) = 0.6</c>, so similarity alone can order results
    /// but can never satisfy <see cref="ConfidenceCalibration.MeetsAcceptanceThreshold"/>.
    /// </summary>
    public double SimilarityOnly { get; init; } = 0.5;
}
