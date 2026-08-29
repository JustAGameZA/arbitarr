namespace Arbitarr.Core.Sources;

/// <summary>
/// Describes the capabilities advertised by an upstream source (Torznab/Newznab "caps").
/// </summary>
/// <param name="SupportedCategories">Category IDs the source advertises support for.</param>
/// <param name="SupportsTvSearch">Whether the source supports TV-specific search parameters.</param>
/// <param name="SupportsMovieSearch">Whether the source supports movie-specific search parameters.</param>
/// <param name="MaxPageSize">The maximum number of results returned per request, if bounded.</param>
/// <param name="SupportedParams">
/// Torznab/Newznab search parameter names (e.g. "q", "season", "ep", "imdbid") this source
/// accepts. When aggregating caps across multiple sources, only params supported by ALL
/// contributing sources should be advertised (intersection) — a param missing from this list
/// for a given source means callers should degrade to keyword search plus local post-filtering
/// for that source; the degradation logic itself is not implemented here, only represented.
/// </param>
/// <param name="SupportsAnimeSearch">
/// Whether the source supports anime as a distinct, selectable search category. Aggregation
/// treats this as true if ANY contributing source reports true (union semantics), so anime
/// remains selectable even if only one upstream provides it.
/// </param>
public sealed record SourceCaps(
    IReadOnlyList<int> SupportedCategories,
    bool SupportsTvSearch,
    bool SupportsMovieSearch,
    int? MaxPageSize,
    IReadOnlyList<string>? SupportedParams = null,
    bool SupportsAnimeSearch = false)
{
    /// <summary>
    /// Torznab/Newznab category IDs considered "book" categories. Per AC5a-i, merged caps must
    /// NEVER advertise any of these regardless of what an individual upstream reports. 7000 is
    /// the standard Torznab "Books" parent category; 7010-7060 are its standard subcategories
    /// (Mags, EBook, Comics, Technical, Foreign, Other) per the Torznab category convention.
    /// </summary>
    public static readonly IReadOnlyList<int> BookCategoryIds = new[] { 7000, 7010, 7020, 7030, 7040, 7050, 7060 };
}
