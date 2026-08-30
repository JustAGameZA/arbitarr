namespace Arbitarr.Core.Sources;

/// <summary>
/// A search request to be issued against an upstream source.
/// </summary>
/// <param name="QueryText">Free-text query term, if any.</param>
/// <param name="Categories">Torznab/Newznab category IDs to restrict the search to.</param>
/// <param name="Limit">Maximum number of results requested.</param>
/// <param name="Offset">Paging offset, for sources that support it.</param>
/// <param name="TvdbId">Sonarr/Radarr's own resolved TVDB series id, when the request carries one.</param>
/// <param name="TmdbId">Sonarr/Radarr's own resolved TMDB id, when the request carries one.</param>
/// <param name="Season">Season number, when the request carries one (id-based requests only).</param>
/// <param name="Episode">Episode number, when the request carries one (id-based requests only).</param>
public sealed record SearchQuery(
    string? QueryText,
    IReadOnlyList<int> Categories,
    int Limit,
    int Offset = 0,
    int? TvdbId = null,
    int? TmdbId = null,
    int? Season = null,
    int? Episode = null);
