using ArrSearcher.Core.Identity;

namespace ArrSearcher.Media.Identity;

/// <summary>
/// Determines whether a release title positively identifies a <see cref="SeriesIdentity"/>, using its
/// full alternate-title set rather than exact equality against <see cref="SeriesIdentity.PrimaryTitle"/>.
/// </summary>
/// <remarks>
/// Exists because release-group renderings, localized titles, and arc-specific alternate names (XEM's
/// season-keyed names map) routinely diverge from a series' primary display title. Matching a release
/// against a canonical identity therefore requires checking the *entire* alternate-title set, not just
/// the primary title — the same reasoning that motivates <see cref="SeriesIdentity.AlternateTitles"/>
/// existing at all.
/// </remarks>
public static class AlternateTitleMatcher
{
    /// <summary>
    /// Tests whether <paramref name="releaseTitle"/> matches the identity's primary title or any of
    /// its alternate titles, using ordinal case-insensitive comparison after trimming.
    /// </summary>
    public static bool Matches(SeriesIdentity identity, string releaseTitle)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(releaseTitle);

        var normalized = releaseTitle.Trim();

        if (string.Equals(identity.PrimaryTitle.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alternate in identity.AlternateTitles)
        {
            if (string.Equals(alternate.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns every title (primary plus alternates) that matched <paramref name="releaseTitle"/>, for
    /// use as <c>MatchEvidence</c> when composing provenance. Empty when nothing matched.
    /// </summary>
    public static IReadOnlyList<string> FindMatchingTitles(SeriesIdentity identity, string releaseTitle)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(releaseTitle);

        var normalized = releaseTitle.Trim();
        var matches = new List<string>();

        if (string.Equals(identity.PrimaryTitle.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            matches.Add(identity.PrimaryTitle);
        }

        foreach (var alternate in identity.AlternateTitles)
        {
            if (string.Equals(alternate.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(alternate);
            }
        }

        return matches;
    }
}
