namespace ArrSearcher.Media.Numbering;

/// <summary>
/// One row of a series' season-keyed arc-title map: binds a story-arc name to the scene season
/// number that carries it, plus the absolute-episode range the arc spans.
/// </summary>
/// <remarks>
/// <para>
/// Modelled directly on XEM's <c>names?origin=tvdb&amp;id=</c> endpoint, which is the data source
/// that resolves the Bleach flagship example: the "Thousand-Year Blood War" arc title is keyed to
/// scene season 17, and a release whose title tokens mention that arc (or an alternate rendering of
/// it) should bind to season 17 rather than any other season that happens to share episode numbers.
/// </para>
/// <para>
/// <see cref="AbsoluteRangeStart"/>/<see cref="AbsoluteRangeEnd"/> are inclusive and are what let
/// arc-relative numbering be converted back to an absolute number for cross-checking against *arr's
/// own absolute-episode data (see <see cref="CandidateNumberingSetBuilder"/>).
/// </para>
/// </remarks>
/// <param name="ArcTitle">The canonical arc name, e.g. "Thousand-Year Blood War".</param>
/// <param name="AlternateArcTitles">Other renderings of the arc name a release might use.</param>
/// <param name="Season">The scene season number this arc is keyed to.</param>
/// <param name="AbsoluteRangeStart">First absolute episode number (inclusive) covered by this arc.</param>
/// <param name="AbsoluteRangeEnd">Last absolute episode number (inclusive) covered by this arc.</param>
public sealed record ArcSeasonBinding(
    string ArcTitle,
    IReadOnlyList<string> AlternateArcTitles,
    int Season,
    int AbsoluteRangeStart,
    int AbsoluteRangeEnd)
{
    /// <summary>
    /// Whether the given absolute episode number falls within this arc's inclusive range.
    /// </summary>
    public bool CoversAbsolute(int absoluteEpisode) =>
        absoluteEpisode >= AbsoluteRangeStart && absoluteEpisode <= AbsoluteRangeEnd;

    /// <summary>
    /// Whether any of this arc's title renderings (canonical or alternate) match the given token,
    /// using ordinal case-insensitive comparison.
    /// </summary>
    public bool MatchesTitleToken(string token)
    {
        if (string.Equals(ArcTitle, token, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alternate in AlternateArcTitles)
        {
            if (string.Equals(alternate, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A series' full season-keyed arc-title map: every known arc binding, in no particular order.
/// </summary>
/// <param name="Bindings">All arc/season bindings known for the series.</param>
public sealed record ArcSeasonMap(IReadOnlyList<ArcSeasonBinding> Bindings)
{
    /// <summary>
    /// Finds the binding whose title (canonical or alternate) matches the given token, or
    /// <see langword="null"/> if no binding matches.
    /// </summary>
    public ArcSeasonBinding? FindByTitleToken(string token) =>
        Bindings.FirstOrDefault(b => b.MatchesTitleToken(token));

    /// <summary>
    /// Finds every binding whose absolute range covers the given absolute episode number. Returns
    /// more than one entry when the map itself is ambiguous at that absolute number (the real XEM
    /// abs-36 collision in the Bleach flagship example produces exactly this).
    /// </summary>
    public IReadOnlyList<ArcSeasonBinding> FindByAbsolute(int absoluteEpisode) =>
        Bindings.Where(b => b.CoversAbsolute(absoluteEpisode)).ToArray();
}
