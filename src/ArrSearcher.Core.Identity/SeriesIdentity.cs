namespace ArrSearcher.Core.Identity;

/// <summary>
/// Canonical, display-title-independent identity for a series.
/// </summary>
/// <remarks>
/// <para>
/// Motivated by the Ghost in the Shell franchise-disambiguation problem: sibling series such as
/// <c>Ghost in the Shell: Arise</c>, <c>Stand Alone Complex</c>, and <c>SAC_2045</c> share most of
/// their title text and overlapping <c>S01E01</c>-style numbering, yet are distinct, non-mergeable
/// works. Fuzzy title matching cannot separate them because the shared tokens dominate the string
/// and the episode numbers agree. Disambiguation therefore requires a canonical identity — a
/// provider ID plus the full set of titles a release might legitimately use — as matcher input,
/// never the display title alone.
/// </para>
/// </remarks>
/// <param name="TvdbId">TheTVDB series ID, when known. Null if unresolved or not applicable.</param>
/// <param name="TmdbId">TheMovieDB series ID, when known. Null if unresolved or not applicable.</param>
/// <param name="PrimaryTitle">The canonical/preferred display title for this series.</param>
/// <param name="AlternateTitles">
/// Additional titles this series is legitimately known by, including localized titles, release-group
/// renderings, and arc-specific alternate names (e.g. XEM's season-keyed alternate-names map for
/// Bleach). Used to positively identify a release as belonging to this series without relying on
/// exact title equality.
/// </param>
public sealed record SeriesIdentity(
    int? TvdbId,
    int? TmdbId,
    string PrimaryTitle,
    IReadOnlyList<string> AlternateTitles);
