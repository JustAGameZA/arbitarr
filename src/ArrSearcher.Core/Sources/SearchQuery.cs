namespace ArrSearcher.Core.Sources;

/// <summary>
/// A search request to be issued against an upstream source.
/// </summary>
/// <param name="QueryText">Free-text query term, if any.</param>
/// <param name="Categories">Torznab/Newznab category IDs to restrict the search to.</param>
/// <param name="Limit">Maximum number of results requested.</param>
/// <param name="Offset">Paging offset, for sources that support it.</param>
public sealed record SearchQuery(
    string? QueryText,
    IReadOnlyList<int> Categories,
    int Limit,
    int Offset = 0);
