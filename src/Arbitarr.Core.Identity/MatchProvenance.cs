namespace Arbitarr.Core.Identity;

/// <summary>
/// A single piece of evidence that contributed to a match decision.
/// </summary>
/// <param name="Description">Human-readable description of the evidence (e.g. "arc token 'Thousand-Year Blood War' matched TVDB season 17 alternate name").</param>
/// <param name="Source">Where this evidence came from (e.g. "XEM names map", "TVDB alternate titles").</param>
public sealed record MatchEvidence(string Description, string Source);

/// <summary>
/// Which upstream resolved a result's identity (AC-M7a: "which identity source resolved it").
/// </summary>
public enum IdentitySource
{
    /// <summary>No source resolved an identity (e.g. lookup failed or was never attempted).</summary>
    None = 0,

    /// <summary>Resolved via the *arr instance's own authoritative <c>/api/v3/episode</c> data.</summary>
    ArrApi = 1,

    /// <summary>Resolved via TheXEM's scene/absolute/season-keyed-names maps.</summary>
    Xem = 2,

    /// <summary>Resolved via the AniDB anime-lists static map.</summary>
    AnimeLists = 3,
}

/// <summary>
/// Distinct, independently-recordable degraded conditions a match can occur under (AC-M6). Flags
/// rather than a single enum because more than one can be true at once (e.g. the cache is absent
/// *and* the upstream is unreachable), and because collapsing them into one generic "degraded"
/// value would erase exactly the distinction AC-M6 requires: cache-absent, source-unreachable, and
/// no-xem-coverage are different failures with different remediations and must remain
/// distinguishable in logs and the match explanation. Also carries <see cref="AmbiguousMapping"/>
/// for AC-M3a, which is a data-shape condition rather than a degradation but is reported through the
/// same additive channel on <see cref="MatchProvenance"/>.
/// </summary>
[Flags]
public enum MatchProvenanceFlags
{
    /// <summary>No degradation or ambiguity — resolution proceeded normally.</summary>
    None = 0,

    /// <summary>
    /// A lookup key matched multiple XEM rows and the names map could not disambiguate between them;
    /// per AC-M3a, none of the competing candidates was admitted as the match.
    /// </summary>
    AmbiguousMapping = 1 << 0,

    /// <summary>
    /// No locally cached data was available for this lookup (AC-M6, distinct from
    /// <see cref="SourceUnreachable"/>: the cache being empty does not imply the upstream is down).
    /// </summary>
    CacheAbsent = 1 << 1,

    /// <summary>
    /// The upstream source could not be reached at all (AC-M6, distinct from
    /// <see cref="CacheAbsent"/>: the upstream being unreachable does not imply no cache exists).
    /// </summary>
    SourceUnreachable = 1 << 2,

    /// <summary>
    /// The series has zero XEM coverage — XEM was reachable and returned data, but has no mapping
    /// entries for this series at all (AC-M6's third distinct state; a legitimate, permanent
    /// condition rather than a transient failure, and eligible for negative caching).
    /// </summary>
    NoXemCoverage = 1 << 3,

    /// <summary>
    /// No candidate in the set satisfied identity for the requested episode (AC-M6a): the result set
    /// is returned in full, but none may be promoted to a high-confidence match.
    /// </summary>
    NoCandidateSatisfiedIdentity = 1 << 4,
}

/// <summary>
/// Records how and why a match was made, so a wrong match can be traced back to its cause.
/// </summary>
/// <remarks>
/// <para>
/// Motivated by both worked examples in the plan: the Bleach case shows that a match can look
/// confident while being wrong (bare absolute numbering resolving to the wrong original-run episode
/// instead of the correct arc), and the Ghost in the Shell case shows that near-identical titles can
/// be silently merged across distinct series. In both cases, the only way to catch and audit a wrong
/// match after the fact is to record exactly which scheme, which evidence, and which confidence
/// produced it — not just the final verdict.
/// </para>
/// <para>
/// <see cref="Flags"/> and <see cref="IdentitySource"/> were added in Step 3a, additively, alongside
/// the original <see cref="Scheme"/>/<see cref="Evidence"/>/<see cref="Confidence"/> shape: AC-M3a
/// requires an inspectable ambiguous-mapping outcome (not one inferred from a null match), and AC-M6
/// requires three distinct degraded states to be independently recorded rather than collapsed into a
/// single generic failure. No existing member was removed or renamed, so no consumer of the original
/// shape is broken.
/// </para>
/// </remarks>
/// <param name="Scheme">The numbering scheme (if any) under which the match was scored, e.g. "ArcRelative".</param>
/// <param name="Evidence">The evidence items that supported this match.</param>
/// <param name="Confidence">Confidence score in the range [0, 1], where 1 is certain.</param>
/// <param name="IdentitySource">Which upstream resolved the series identity behind this match, if any.</param>
/// <param name="Flags">
/// Ambiguity/degradation flags in effect for this result. <see cref="MatchProvenanceFlags.None"/> when
/// resolution was clean.
/// </param>
public sealed record MatchProvenance(
    string Scheme,
    IReadOnlyList<MatchEvidence> Evidence,
    double Confidence,
    IdentitySource IdentitySource = IdentitySource.None,
    MatchProvenanceFlags Flags = MatchProvenanceFlags.None);
