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
/// <param name="Season">Season number, when the request carries one (with or without an id, #104).</param>
/// <param name="Episode">Episode number, when the request carries one (with or without an id, #104).</param>
/// <param name="Type">
/// The <c>t=</c> mode the inbound request asked for, which selects the upstream <c>t=</c> (#104).
/// Optional and defaulted to <see cref="SearchType.Search"/> — unlike <paramref name="Protocol"/>,
/// whose default would silently route a usenet caller to the torrent-only endpoint, an unset value
/// here reproduces exactly the pre-#104 behaviour of every construction site, so a caller that has
/// no inbound mode of its own (a background refresher, a test) is not forced to invent one.
/// </param>
/// <param name="Absolute">
/// arb-u1c: the ABSOLUTE episode number this request is for, when one can be determined — for
/// Sonarr's anime shape that is the bare number it sends as <c>q</c> alongside a <c>tvdbid</c>.
///
/// <para><b>THIS EXISTS BECAUSE THE CACHE KEY NEEDS IT, not only because the upstream query does.</b>
/// <c>SearchResultCacheStage.BuildNumbering</c> derives its numbering candidate from
/// <paramref name="Season"/>/<paramref name="Episode"/>, and Sonarr's anime request carries NEITHER
/// — the episode rides in <c>q</c>. The two-age key also ignores <c>q</c> whenever a tvdbid is
/// present, deliberately: that is what collapses the many textual spellings of one episode onto one
/// row. Without this field every absolute-episode search of one series therefore collapses onto a
/// SINGLE cache row, so a search for episode 92 is served the cached set for episode 91. Populating
/// this from the request separates them WITHOUT reintroducing <c>q</c> into the id-bearing key,
/// which would undo the collapse the key exists to provide.</para>
///
/// <para>Null where no absolute number applies (a plain text search, a movie, a seasonal
/// <c>S07E01</c> request) — which is every pre-existing construction site, so an unset value
/// reproduces exactly the previous key for all of them.</para>
/// </param>
/// <param name="ResolvedTitle">
/// arb-u1c: the series title an <c>IIdentityResolver</c> resolved this request's provider id to,
/// when resolution succeeded and was unambiguous.
///
/// <para><b>WHY THE TITLE TRAVELS RATHER THAN THE SOURCE RESOLVING IT.</b> Identity resolution
/// needs Sonarr's address and key; a source adapter has neither and must not grow them. Carrying
/// the resolved title on the query keeps the adapter a pure translator of an already-decided
/// request into one upstream URL, and keeps <c>Arbitarr.Api</c> free of any reference to
/// <c>Arbitarr.Media</c> — the composition root wires the implementation, everything else sees only
/// this <c>Core</c> contract.</para>
///
/// <para>Null when nothing was resolved: an unconfigured or unreachable Sonarr, a series it does not
/// track, a request with no id, or — per ADR 0002 — a lookup whose candidates could not be
/// separated, where admitting NO title is the correct answer rather than picking one. That null is
/// not an error state: it degrades to exactly the behaviour that shipped before this field existed,
/// which for Sonarr's anime shape is <c>NzbHydraSource</c> withholding the bare number and sending
/// the id alone.</para>
/// </param>
public sealed record SearchQuery(
    string? QueryText,
    IReadOnlyList<int> Categories,
    int Limit,
    SearchProtocol Protocol,
    int Offset = 0,
    int? TvdbId = null,
    int? TmdbId = null,
    int? Season = null,
    int? Episode = null,
    SearchType Type = SearchType.Search,
    int? Absolute = null,
    string? ResolvedTitle = null);
