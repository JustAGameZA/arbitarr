using System.Text.RegularExpressions;

namespace Arbitarr.Ai.Normalization;

/// <summary>
/// Reduces a raw release title down to a bare series name, so the result can be exact-matched
/// against <c>Arbitarr.Media.Identity.AlternateTitleMatcher</c> (M6/Media), which performs only
/// trim + case-insensitive equality against a <c>SeriesIdentity</c>'s <c>PrimaryTitle</c>/
/// <c>AlternateTitles</c> and does no stripping of its own. Without this extraction step, a raw
/// title such as "Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB" can never match
/// a clean identity title like "Bleach".
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="TitleNormalizer"/>, which only strips deny-listed noise
/// tokens (release group, source tags) and keeps the episode marker intact — it does not reduce a
/// title to a bare series name. This extractor instead cuts the title at the first recognizable
/// episode/season marker (e.g. <c>S17E45</c>, <c>S01</c>) and trims trailing separators, leaving
/// everything before it untouched (so it works whether given a raw or already-normalized title).
/// </remarks>
public static class SeriesNameExtractor
{
    // Matches the first standalone SxxEyy or Sxx marker (word-boundary delimited so it doesn't
    // match inside an unrelated token), case-insensitive per genre convention (Bleach uses
    // uppercase "S17E45"; other releases vary).
    private static readonly Regex EpisodeMarkerPattern = new(
        @"\bS\d{1,3}(E\d{1,3})?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the bare series name from <paramref name="releaseTitle"/> by cutting at the first
    /// season/episode marker and trimming residual separators. Returns the trimmed input unchanged
    /// if no marker is found.
    /// </summary>
    public static string Extract(string releaseTitle)
    {
        ArgumentNullException.ThrowIfNull(releaseTitle);

        var trimmed = releaseTitle.Trim();
        var match = EpisodeMarkerPattern.Match(trimmed);

        var seriesPortion = match.Success ? trimmed[..match.Index] : trimmed;

        return seriesPortion.TrimEnd('.', '-', '_', ' ').Replace('.', ' ').Replace('_', ' ')
            .Trim();
    }
}
