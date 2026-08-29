namespace Arbitarr.Core.Identity;

/// <summary>
/// One numbering candidate under consideration during matching, together with the provenance that
/// applies to it specifically. Used to report competing candidates when a lookup is ambiguous
/// (AC-M3a) — each candidate carries its own evidence/confidence/flags rather than the matcher
/// picking a winner and discarding the rest.
/// </summary>
/// <param name="Candidate">The numbering interpretation this entry describes.</param>
/// <param name="Provenance">Evidence, confidence, and flags specific to this candidate.</param>
public sealed record AnnotatedNumberingCandidate(
    NumberingCandidate Candidate,
    MatchProvenance Provenance);

/// <summary>
/// The outcome of matching a release's candidate numbering against a resolved series identity.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Matched"/> alone cannot express AC-M3a's required outcome: "given a lookup key that
/// matches multiple XEM rows, the resolver returns competing annotated candidates and — where the
/// names map cannot disambiguate — admits none of them, flagging <c>ambiguous-mapping</c>, rather
/// than selecting one." A <see langword="null"/> <see cref="Matched"/> is ambiguous on its own
/// between "nothing matched at all" (AC-M6a) and "multiple things matched and none could be safely
/// chosen" (AC-M3a) — two different conditions with different remediations. <see cref="Candidates"/>
/// makes the competing set inspectable in both cases, and <see cref="Provenance"/>.<c>Flags</c>
/// (via <see cref="MatchProvenanceFlags.AmbiguousMapping"/> vs.
/// <see cref="MatchProvenanceFlags.NoCandidateSatisfiedIdentity"/>) distinguishes which condition
/// applies without the caller having to infer it from null-ness.
/// </para>
/// </remarks>
/// <param name="Matched">The specific numbering candidate chosen as correct, or <see langword="null"/> if none could be confidently selected (whether because none satisfied identity, or because multiple competed and the ambiguity could not be resolved).</param>
/// <param name="Provenance">Explains how/why <see cref="Matched"/> was chosen (or why none was), for auditability. Carries the overall result's flags, e.g. <see cref="MatchProvenanceFlags.AmbiguousMapping"/>.</param>
/// <param name="Candidates">
/// Every numbering candidate that was considered, each individually annotated. When
/// <see cref="Matched"/> is <see langword="null"/> due to ambiguity, this contains the competing
/// candidates that could not be disambiguated (AC-M3a) — never silently dropped.
/// </param>
public sealed record EpisodeMatchResult(
    NumberingCandidate? Matched,
    MatchProvenance Provenance,
    IReadOnlyList<AnnotatedNumberingCandidate> Candidates);

/// <summary>
/// Matches a release's <see cref="CandidateNumberingSet"/> against a <see cref="SeriesIdentity"/> to
/// select the correct numbering interpretation.
/// </summary>
/// <remarks>
/// <para>
/// Exists because of the Bleach arc-numbering example: a release's numbering is ambiguous on its own
/// — the same bare absolute or season/episode tuple can resolve to two different, non-interchangeable
/// episodes depending on which story arc is in play. Correct resolution requires arc identification
/// first (matching the release's title tokens against a series' arc/season alternate names), and only
/// then selecting the season-qualified numbering candidate that arc implies. This interface defines
/// the contract only — no resolution logic ships here; see the Step 3a implementations in
/// <c>Arbitarr.Media</c>.
/// </para>
/// </remarks>
public interface IEpisodeMatcher
{
    /// <summary>
    /// Selects the correct numbering candidate for a release given its resolved series identity.
    /// </summary>
    /// <param name="identity">The series the release has already been identified as belonging to.</param>
    /// <param name="candidates">The release's plausible numbering interpretations.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The match result, including provenance regardless of whether a match was found.</returns>
    Task<EpisodeMatchResult> MatchAsync(
        SeriesIdentity identity,
        CandidateNumberingSet candidates,
        CancellationToken cancellationToken = default);
}
