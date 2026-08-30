namespace Arbitarr.Api.Search;

/// <summary>
/// Security-m3 MEDIUM #4: bounds attacker/client-controlled <c>tvdbid</c>/<c>tmdbid</c>/
/// <c>season</c>/<c>ep</c> query-string inputs at the endpoint boundary, independent of what any
/// downstream component does with them. Unvalidated, these bind straight through as <c>int?</c>
/// and each distinct out-of-range value (including negatives and <see cref="int.MinValue"/>)
/// widens <see cref="Arbitarr.Core.Identity.SearchCacheKeyBuilder"/>'s key space with a fresh
/// cache row. Values outside a plausible real-world range are treated as absent, so the query
/// falls back to its next identity signal (e.g. the title-set/q-text path) instead of minting a
/// new row per garbage value.
/// </summary>
public static class IdParamClamp
{
    /// <summary>Plausible upper bound for a TVDB/TMDB provider id.</summary>
    public const int MaxProviderId = 9_999_999;

    /// <summary>Plausible upper bound for a season number.</summary>
    public const int MaxSeason = 9_999;

    /// <summary>Plausible upper bound for an episode number.</summary>
    public const int MaxEpisode = 99_999;

    public static int? ClampProviderId(int? id) =>
        id is null or <= 0 or > MaxProviderId ? null : id;

    public static int? ClampSeason(int? season) =>
        season is null or < 0 or > MaxSeason ? null : season;

    public static int? ClampEpisode(int? episode) =>
        episode is null or < 0 or > MaxEpisode ? null : episode;
}
