using System.Text.RegularExpressions;

namespace Arbitarr.Core.Identity.Titles;

/// <summary>
/// Reduces a raw release title down to a bare series-name portion, so the result can be
/// exact-matched (after trim + case-insensitive comparison) against a <see cref="SeriesIdentity"/>'s
/// <c>PrimaryTitle</c>/<c>AlternateTitles</c> — e.g. by <c>Arbitarr.Media.Identity.AlternateTitleMatcher</c>,
/// which performs only that trim+equality check and does no stripping of its own. Without this
/// extraction step, a raw title such as "Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H
/// 264-playWEB" can never match a clean identity title like "Bleach".
/// </summary>
/// <remarks>
/// Lives in <c>Arbitarr.Core.Identity</c> (references only <c>Arbitarr.Core</c>) rather than in
/// <c>Arbitarr.Ai</c> or <c>Arbitarr.Media</c>, since both of those already reference
/// <c>Arbitarr.Core.Identity</c> and either direction of an Ai&lt;-&gt;Media reference is forbidden
/// (AC6a). This type carries no Ai or Media types in its signature and is a pure, deterministic
/// function of its input string.
/// </remarks>
public static class SeriesNameExtractor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    // A leading release-group tag: "[Group] " or "[Group]" (no trailing space) at the very start
    // of the title, e.g. "[ToonsHub] BLEACH ..." or "[TESHI]Bleach-236". Also strips a leading
    // "(Group) " form for symmetry.
    private static readonly Regex LeadingGroupTagPattern = new(
        @"^\s*[\[(][^\]\)]+[\])]\s*",
        RegexOptions.NonBacktracking, MatchTimeout);

    // A trailing "-Group" release-group tag at the very end of the title, e.g. "...x265-MeGusta".
    // Requires the group token to contain no whitespace, so it doesn't eat a " - 329"
    // absolute-episode separator.
    private static readonly Regex TrailingGroupTagPattern = new(
        @"-\S+\s*$",
        RegexOptions.NonBacktracking, MatchTimeout);

    // Season/episode/absolute-episode/pack markers — the strongest, least-ambiguous cut points.
    // Checked first (as a standalone pattern) so a title that legitimately contains a bare 4-digit
    // year (e.g. "Ghost in the Shell SAC 2045 S02E13...") still cuts at S02E13 rather than at the
    // year, even though the year occurs earlier in the string.
    private static readonly Regex EpisodeMarkerCutPointPattern = new(
        string.Join('|',
        [
            // SxxEyy or a season-only pack marker (Sxx).
            @"\bS\d{1,3}(?:E\d{1,3})?\b",
            // ##x## (e.g. "5x12").
            @"\b\d{1,3}x\d{1,3}\b",
            // Absolute episode number introduced by " - " or a bare "-" (e.g. "Bleach - 329").
            @"\s*-\s*\d{1,4}\b",
            // Season/batch pack markers.
            @"\b(?:Complete|Batch|Season\s*Pack)\b",
        ]),
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, MatchTimeout);

    // Fallback cut points used only when no episode marker is present (e.g. a movie-form title):
    // a 4-digit year, resolution, codec, or source token. Alternatives are tried left-to-right at
    // every position, so whichever token occurs earliest wins.
    private static readonly Regex MetadataTokenCutPointPattern = new(
        string.Join('|',
        [
            // A 4-digit year, bare or wrapped in parens/brackets (movie form, or a year embedded
            // mid-title such as a remaster/reboot marker).
            @"[\[(]\s*(?:19|20)\d{2}\s*[\])]",
            @"\b(?:19|20)\d{2}\b",
            // Resolution.
            @"\b(?:480|720|1080|2160)p\b",
            @"\b4K\b",
            // Codec.
            @"\bx26[45]\b",
            @"\bH\s?26[45]\b",
            @"\bHEVC\b",
            @"\bAV1\b",
            @"\bXviD\b",
            // Source.
            @"\bWEB-?DL\b",
            @"\bWEBRip\b",
            @"\bBlu-?Ray\b",
            @"\bBDRip\b",
            @"\bHDTV\b",
            @"\bDSNP\b",
            @"\bAMZN\b",
            @"\bNF\b",
        ]),
        RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, MatchTimeout);

    /// <summary>
    /// Extracts the bare series-name portion from <paramref name="releaseTitle"/>: strips a leading
    /// release-group tag, cuts at the first season/episode/absolute-episode/pack/year/resolution/
    /// codec/source token, strips a trailing "-Group" tag from the remainder, normalizes residual
    /// dot/underscore separators to spaces, and collapses/trims whitespace. Returns the trimmed
    /// (post-tag-strip) input unchanged if no cut point is found. Returns <c>null</c> if nothing but
    /// noise remains.
    /// </summary>
    public static string? Extract(string releaseTitle)
    {
        ArgumentNullException.ThrowIfNull(releaseTitle);

        var withoutGroupTag = LeadingGroupTagPattern.Replace(releaseTitle.Trim(), string.Empty);
        var trimmed = withoutGroupTag.Trim();

        var match = EpisodeMarkerCutPointPattern.Match(trimmed);
        if (!match.Success)
        {
            match = MetadataTokenCutPointPattern.Match(trimmed);
        }

        var seriesPortion = match.Success ? trimmed[..match.Index] : trimmed;

        seriesPortion = TrailingGroupTagPattern.Replace(seriesPortion, string.Empty);

        var normalized = Regex.Replace(
            seriesPortion.Replace('.', ' ').Replace('_', ' '),
            @"\s+",
            " ",
            RegexOptions.None,
            MatchTimeout).Trim(' ', '-');

        return normalized.Length == 0 ? null : normalized;
    }
}
