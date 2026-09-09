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
    string? ResolvedTitle = null)
{
    /// <summary>
    /// Whether this request is Sonarr's anime episode shape: an id-scoped TV search whose
    /// <c>q</c> is a bare absolute episode number. When true, <paramref name="absolute"/> carries
    /// that number.
    /// </summary>
    /// <remarks>
    /// <para><b>ONE PREDICATE, BECAUSE TWO DRIFTED.</b> This question is asked in two places for two
    /// reasons — <c>SearchEndpoint</c> asks it to decide what goes in the cache key and whether to
    /// spend an identity lookup, and <c>NzbHydraSource.BuildSearchUri</c> asks it to decide how to
    /// spell the upstream <c>q</c> — and arb-u1c shipped them as two functions that were supposed to
    /// agree. They did not: only one had an overflow guard, so a 20-digit <c>q</c> was classified as
    /// a bare number by the source (which withheld it) and as ordinary text by the endpoint (which
    /// recorded no absolute). They also disagreed about a <c>t=search</c> request carrying a tvdbid,
    /// which the source treats as a tvsearch and the endpoint did not. Two call sites deciding the
    /// upstream URL and the cache key from different answers is how one episode's results get
    /// cached under another's, so the predicate lives here, on the query it is about, and both sides
    /// call it.</para>
    ///
    /// <para><b>THE OVERFLOW GUARD IS PART OF THE ANSWER, not a caller's afterthought.</b> A run of
    /// digits too long to be an <see cref="int"/> is not an episode number; it is text that happens
    /// to be numeric. Answering false for it means the source sends it as an ordinary <c>q</c> — the
    /// correct handling for text — rather than withholding a <c>q</c> it could not parse, and means
    /// no nonsense value is recorded in the cache key.</para>
    ///
    /// <para>Scoped deliberately narrowly, and this scoping is load-bearing: only a TV search that
    /// will actually emit its <c>tvdbid</c>, and only when the text is ENTIRELY ASCII digits. A
    /// textual <c>q</c> next to an id (<c>tvdbid=74796&amp;q=bleach</c>) is a title and stays; a
    /// numeric <c>q</c> with no id has nothing else to identify the series and stays, since dropping
    /// it would turn the request into a category-wide feed. <c>char.IsAsciiDigit</c> rather than
    /// <c>char.IsDigit</c> because Sonarr formats the number with the invariant culture: only ASCII
    /// digits are the shape being matched, and a non-ASCII numeral is text like any other.</para>
    /// </remarks>
    public bool IsIdScopedAbsoluteNumberQuery(out int absolute)
    {
        absolute = 0;

        // The source treats a tvdbid-bearing request as a tvsearch whatever its inbound t= said, so
        // the id's presence — not the declared mode alone — is what makes this shape. Asking it this
        // way is what keeps this answer identical to the one the upstream URL is built from.
        if (TvdbId is null || Type == SearchType.Movie)
        {
            return false;
        }

        var queryText = QueryText?.Trim() ?? string.Empty;
        if (queryText.Length == 0 || !queryText.All(char.IsAsciiDigit))
        {
            return false;
        }

        return int.TryParse(
            queryText,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out absolute);
    }
}
