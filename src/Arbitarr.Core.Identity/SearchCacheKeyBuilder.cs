namespace Arbitarr.Core.Identity;

/// <summary>
/// Builds the two-age cache's <c>QueryKey</c> from a resolved series identity, rather than from raw
/// query text (AC23b(4)/M3-9).
/// </summary>
/// <remarks>
/// <para>
/// Motivated by two opposing failure modes a naive query-string key would produce. The Bleach
/// arc-numbering example requires collapsing: <c>S17E36</c>, <c>17x36</c>, and <c>17x36 (402)</c> are
/// three different textual renderings of the exact same episode once the numbering candidate is
/// resolved, and must share one cache entry / one upstream call rather than three. The Ghost in the
/// Shell franchise-disambiguation example requires separating: <c>Ghost in the Shell: Arise</c>,
/// <c>Stand Alone Complex</c>, and <c>SAC_2045</c> share most of their title text and can carry
/// overlapping season/episode numbers, yet are distinct, non-mergeable works that must produce three
/// distinct cache entries. A key built from <see cref="SeriesIdentity"/> (provider ID first, falling
/// back to titles only when no ID is known) plus the *resolved* numbering candidate, rather than
/// display text, satisfies both directions at once: equivalent numbering renderings resolve to the
/// same candidate and collapse, while distinct series never share a provider ID or title set and so
/// never collapse — "a key that only separates, or only collapses, satisfies half of AC23b(4)."
/// </para>
/// </remarks>
public static class SearchCacheKeyBuilder
{
    /// <summary>
    /// Builds the cache <c>QueryKey</c> for a search against a resolved series identity and numbering
    /// candidate, scoped by category (and, where present, resolution profile) so that requests for the
    /// same episode under different filters do not collapse onto one entry.
    /// </summary>
    /// <param name="identity">The series the search has already been resolved to.</param>
    /// <param name="candidate">The resolved numbering candidate (season/episode/absolute) being searched for.</param>
    /// <param name="categories">Torznab/Newznab category IDs the search was restricted to.</param>
    /// <param name="profile">
    /// Optional resolution-profile discriminator (e.g. a quality/language profile), for callers that
    /// have one. No such concept exists elsewhere in the codebase yet (it is out of scope until M4's
    /// API-key-profile work); this parameter exists so a future profile value can be threaded through
    /// without reshaping the key, and defaults to null (no profile scoping) until then.
    /// </param>
    public static string Build(
        SeriesIdentity identity,
        NumberingCandidate candidate,
        IReadOnlyList<int> categories,
        string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(categories);

        var seriesToken = BuildSeriesToken(identity);
        var numberingToken = BuildNumberingToken(candidate);
        var categoryToken = categories.Count == 0
            ? "none"
            : string.Join(",", categories.OrderBy(c => c));
        var profileToken = string.IsNullOrEmpty(profile) ? "default" : profile;

        return $"series:{seriesToken}:{numberingToken}:cat={categoryToken}:profile={profileToken}";
    }

    private static string BuildSeriesToken(SeriesIdentity identity)
    {
        // Provider ID first: it is the only input immune to the Ghost in the Shell problem (shared
        // title tokens). Fall back to the full title set (primary + alternates, order-independent)
        // only when no ID is known, so two identities with different ID-less title sets still
        // separate rather than colliding on an empty/default token.
        if (identity.TvdbId is int tvdbId)
        {
            return $"tvdb:{tvdbId}";
        }

        if (identity.TmdbId is int tmdbId)
        {
            return $"tmdb:{tmdbId}";
        }

        var titles = new[] { identity.PrimaryTitle }
            .Concat(identity.AlternateTitles)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal);

        return $"titles:{string.Join("|", titles)}";
    }

    private static string BuildNumberingToken(NumberingCandidate candidate)
    {
        // Season/episode/absolute from the *resolved* candidate, not the raw release text: this is
        // exactly what collapses S17E36 / 17x36 / 17x36(402) onto one token once IEpisodeMatcher has
        // picked the same candidate for all three.
        var season = candidate.Season is int s ? s.ToString() : "none";
        var absolute = candidate.Absolute is int a ? a.ToString() : "none";
        return $"{candidate.Scheme}:s={season}:e={candidate.Episode}:abs={absolute}";
    }
}
