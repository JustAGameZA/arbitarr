namespace Arbitarr.Media.Providers;

/// <summary>
/// One <c>&lt;anime&gt;</c> entry from AniDB's <c>anime-lists</c> static XML mapping: the identity
/// correspondence between an AniDB series and its TVDB/TMDB counterpart, plus any alternate names
/// recorded for it.
/// </summary>
/// <param name="AniDbId">AniDB series id.</param>
/// <param name="TvdbId">Corresponding TVDB series id, when mapped.</param>
/// <param name="TmdbId">Corresponding TMDB id, when mapped.</param>
/// <param name="DefaultTvdbSeason">
/// The TVDB season this AniDB series' numbering defaults to, when the mapping specifies one.
/// </param>
/// <param name="Names">Alternate names recorded for this entry.</param>
public sealed record AnimeListsEntry(
    int AniDbId,
    int? TvdbId,
    int? TmdbId,
    int? DefaultTvdbSeason,
    IReadOnlyList<string> Names);

/// <summary>
/// The parsed contents of the AniDB <c>anime-lists</c> static XML mapping.
/// </summary>
/// <param name="Entries">Every entry parsed from the document.</param>
public sealed record AnimeListsDataset(IReadOnlyList<AnimeListsEntry> Entries);
