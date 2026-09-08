namespace Arbitarr.Core.Sources;

/// <summary>
/// A search request to be issued against an upstream source.
/// </summary>
/// <param name="QueryText">Free-text query term, if any.</param>
/// <param name="Categories">Torznab/Newznab category IDs to restrict the search to.</param>
/// <param name="Limit">Maximum number of results requested.</param>
/// <param name="Protocol">
/// The indexer protocol family the request arrived on, which selects the upstream endpoint (#99).
/// Positional and non-optional on purpose: a defaulted value here would silently route every
/// construction site that forgot it to the torrent-only endpoint, which is the exact bug #99
/// reports. Omitting it is a compile error instead. See <see cref="SearchProtocol"/> for why this
/// is not <see cref="Arbitarr.Core.Releases.ProtocolKind"/>.
/// </param>
/// <param name="Offset">Paging offset, for sources that support it.</param>
/// <param name="TvdbId">Sonarr/Radarr's own resolved TVDB series id, when the request carries one.</param>
/// <param name="TmdbId">Sonarr/Radarr's own resolved TMDB id, when the request carries one.</param>
/// <param name="Season">Season number, when the request carries one (id-based requests only).</param>
/// <param name="Episode">Episode number, when the request carries one (id-based requests only).</param>
public sealed record SearchQuery(
    string? QueryText,
    IReadOnlyList<int> Categories,
    int Limit,
    SearchProtocol Protocol,
    int Offset = 0,
    int? TvdbId = null,
    int? TmdbId = null,
    int? Season = null,
    int? Episode = null);
