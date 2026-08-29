namespace ArrSearcher.Core.Identity;

/// <summary>
/// The numbering scheme under which a <see cref="NumberingCandidate"/> was expressed.
/// </summary>
public enum NumberingScheme
{
    /// <summary>Season/episode numbers relative to a story arc rather than the original TVDB run.</summary>
    ArcRelative,

    /// <summary>Season/episode numbers as published by TheTVDB for the original run.</summary>
    TvdbSeasonal,

    /// <summary>A single absolute episode number with no season component.</summary>
    Absolute,
}

/// <summary>
/// One interpretation of a release's season/episode numbering under a single <see cref="NumberingScheme"/>.
/// </summary>
/// <param name="Scheme">Which numbering scheme this candidate was decoded under.</param>
/// <param name="Season">Season number under this scheme, if the scheme has one.</param>
/// <param name="Episode">Episode number under this scheme.</param>
/// <param name="Absolute">Absolute episode number, if known or derivable under this scheme.</param>
public sealed record NumberingCandidate(
    NumberingScheme Scheme,
    int? Season,
    int Episode,
    int? Absolute);

/// <summary>
/// The set of plausible numbering interpretations for a single release, prior to arc identification.
/// </summary>
/// <remarks>
/// <para>
/// Motivated by the Bleach arc-numbering example: a release labelled <c>17x36(402)</c> decomposes to
/// scene season 17, scene episode 36, absolute 402 — but the same bare numbers can also resolve, via
/// a different scheme, to an entirely different and *wrong* episode from the original run (XEM's own
/// data contains two distinct rows both carrying absolute 36). Arc-relative, TVDB-seasonal, and
/// absolute numbering can all apply to the same release simultaneously, and which one is correct
/// depends on identifying the story arc first. This type therefore represents numbering as a set of
/// *candidates* to be disambiguated later (by <see cref="IEpisodeMatcher"/> against a
/// <see cref="SeriesIdentity"/>'s arc/season information) — never as a single resolved answer chosen
/// up front, which is precisely the mistake that produces a confident-but-wrong match.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// The bare <c>(season=1, episode=arc_relative_ep)</c> candidate is deliberately never generated
/// (AC-M1's regression fixture: the Bleach <c>S01E36</c> release-group rendering must not produce a
/// scene-season-1 candidate, since XEM has no scene season 1 in the Thousand-Year-Blood-War range and
/// generating one would silently resolve to a different, wrong episode). This is enforced as a
/// generation-time rule by the candidate-set builder in <c>ArrSearcher.Media</c> — it is not a shape
/// constraint on this type. <see cref="NumberingCandidate"/> can still represent a genuine season-1
/// candidate when one is actually warranted (e.g. a real TVDB-seasonal season 1 episode); what is
/// excluded is specifically the *bare, unqualified* arc-relative-as-season-1 guess.
/// </para>
/// </remarks>
/// <param name="Candidates">All plausible numbering interpretations for the release, unranked.</param>
public sealed record CandidateNumberingSet(IReadOnlyList<NumberingCandidate> Candidates);
