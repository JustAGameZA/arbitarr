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
    // A leading release-group tag: "[Group] " or "[Group]" (no trailing space) at the very start
    // of the title, e.g. "[ToonsHub] BLEACH ..." or "[TESHI]Bleach-236". Also strips a leading
    // "(Group) " form for symmetry.
    private static readonly Regex LeadingGroupTagPattern = new(
        @"^\s*[\[(][^\]\)]+[\])]\s*",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    // The first standalone season/episode marker: SxxEyy, Sxx (season pack), or an absolute
    // episode number introduced by " - " or a bare "-" (e.g. "Bleach - 329", "Bleach-236").
    // Word-boundary delimited so it doesn't match inside an unrelated token.
    private static readonly Regex CutPointPattern = new(
        @"(?:\bS\d{1,3}(?:E\d{1,3})?\b)|(?:\s*-\s*\d{1,4}\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Extracts the bare series-name portion from <paramref name="releaseTitle"/>: strips a leading
    /// release-group tag, cuts at the first season/episode/absolute-episode marker, normalizes
    /// residual dot/underscore separators to spaces, and collapses/trim whitespace. Returns the
    /// trimmed (post-tag-strip) input unchanged if no cut point is found. Returns <c>null</c> if
    /// nothing but noise remains.
    /// </summary>
    public static string? Extract(string releaseTitle)
    {
        ArgumentNullException.ThrowIfNull(releaseTitle);

        var withoutGroupTag = LeadingGroupTagPattern.Replace(releaseTitle.Trim(), string.Empty);
        var trimmed = withoutGroupTag.Trim();

        var match = CutPointPattern.Match(trimmed);
        var seriesPortion = match.Success ? trimmed[..match.Index] : trimmed;

        var normalized = Regex.Replace(
            seriesPortion.Replace('.', ' ').Replace('_', ' '),
            @"\s+",
            " ",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(200)).Trim(' ', '-');

        return normalized.Length == 0 ? null : normalized;
    }
}
