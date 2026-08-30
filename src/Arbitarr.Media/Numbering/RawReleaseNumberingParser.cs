using System.Text.RegularExpressions;

namespace Arbitarr.Media.Numbering;

/// <summary>
/// Extracts the <see cref="RawReleaseNumbering"/> a scene release title carries on its face:
/// <c>SxxEyy</c> scene numbering, a dash-separated absolute episode number, and whichever arc
/// title (or alternate) from the supplied <see cref="ArcSeasonMap"/> the title spells out.
/// Nothing here is corroborated; that is the job of the builder and scorer downstream.
/// </summary>
public static partial class RawReleaseNumberingParser
{
    [GeneratedRegex(@"\bS(?<season>\d{1,2})E(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SceneNumbering();

    // " - 329", "]Bleach-236": an absolute episode number set off by a dash, not followed by more
    // digits or a resolution suffix (so "1080p" and "S17E36" never match).
    [GeneratedRegex(@"(?:\s-\s|(?<=[A-Za-z\]\)])-)(?<absolute>\d{1,4})(?![\dp])", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteNumbering();

    public static RawReleaseNumbering Parse(string releaseTitle, ArcSeasonMap? arcMap)
    {
        ArgumentNullException.ThrowIfNull(releaseTitle);

        var normalized = releaseTitle.Replace('.', ' ').Replace('_', ' ');

        int? sceneSeason = null;
        int? sceneEpisode = null;
        var scene = SceneNumbering().Match(normalized);
        if (scene.Success)
        {
            sceneSeason = int.Parse(scene.Groups["season"].Value, System.Globalization.CultureInfo.InvariantCulture);
            sceneEpisode = int.Parse(scene.Groups["episode"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        int? absolute = null;
        var abs = AbsoluteNumbering().Match(normalized);
        if (abs.Success)
        {
            absolute = int.Parse(abs.Groups["absolute"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return new RawReleaseNumbering(sceneSeason, sceneEpisode, absolute, FindArcTitleToken(normalized, arcMap));
    }

    private static string? FindArcTitleToken(string normalizedTitle, ArcSeasonMap? arcMap)
    {
        if (arcMap is null)
        {
            return null;
        }

        foreach (var binding in arcMap.Bindings)
        {
            if (normalizedTitle.Contains(binding.ArcTitle, StringComparison.OrdinalIgnoreCase))
            {
                return binding.ArcTitle;
            }

            foreach (var alternate in binding.AlternateArcTitles)
            {
                if (normalizedTitle.Contains(alternate, StringComparison.OrdinalIgnoreCase))
                {
                    return alternate;
                }
            }
        }

        return null;
    }
}
