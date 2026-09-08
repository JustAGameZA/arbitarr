namespace Arbitarr.Core.Sources;

/// <summary>
/// What KIND of search the inbound request asked for — the Newznab/Torznab <c>t=</c> mode — as
/// distinct from which ids happened to accompany it. This is what selects the upstream <c>t=</c>
/// (#104): NZBHydra2 accepts <c>t=tvsearch&amp;q=…&amp;season=N&amp;ep=M</c> with no id at all (it
/// issues exactly that shape to indexers itself as its own fallback query), so deriving the mode
/// from the ids alone silently downgraded every q-only episode search to a plain <c>t=search</c>
/// and threw the episode selector away.
/// </summary>
/// <remarks>
/// <para>
/// <c>t=caps</c> is deliberately NOT a member: capability negotiation is answered by
/// <c>CapsEndpoint</c> before a <see cref="SearchQuery"/> is ever built, so it is not a search kind
/// and giving it one would invite a construction site to route a caps request down the search path.
/// </para>
/// <para>
/// CLAUDE.md §3: <see cref="Parse"/> matches the wire names EXPLICITLY and never uses
/// <c>Enum.TryParse</c>. That helper accepts the numeric form, so a caller sending
/// <c>t=1</c> would select a mode through an input shape no Newznab client is documented to have.
/// <c>Enum.IsDefined</c> does not close it either (<c>1</c> IS defined) and neither does trimming
/// (<c>" 1 "</c> and <c>"+1"</c> parse too). Matching the names closes the wire format by
/// construction.
/// </para>
/// </remarks>
public enum SearchType
{
    /// <summary>
    /// A plain free-text search (<c>t=search</c>). The zero value on purpose: it is the mode every
    /// pre-#104 caller effectively requested, so a construction site that does not set this member
    /// keeps exactly today's behaviour rather than silently gaining an episode selector.
    /// </summary>
    Search = 0,

    /// <summary>A TV search (<c>t=tvsearch</c>): accepts <c>tvdbid</c>, <c>season</c> and <c>ep</c>.</summary>
    TvSearch = 1,

    /// <summary>A movie search (<c>t=movie</c>): accepts <c>tmdbid</c>.</summary>
    Movie = 2,
}

/// <summary>Parsing of the inbound <c>t=</c> wire value into a <see cref="SearchType"/>.</summary>
public static class SearchTypeParser
{
    /// <summary>
    /// Maps an inbound Newznab/Torznab <c>t=</c> value to its <see cref="SearchType"/>. Unknown,
    /// missing and numeric values all fall back to <see cref="SearchType.Search"/> — the behaviour
    /// before #104 — rather than being rejected, because an unrecognised mode has always been
    /// served as a plain search and an *arr client must not start receiving errors for one.
    /// </summary>
    public static SearchType Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "tvsearch" => SearchType.TvSearch,
        "movie" => SearchType.Movie,
        // Every other value, "search" included, is a plain search. Named explicitly rather than
        // parsed (CLAUDE.md §3): Enum.TryParse would additionally accept "1"/"2"/"TvSearch".
        _ => SearchType.Search,
    };
}
