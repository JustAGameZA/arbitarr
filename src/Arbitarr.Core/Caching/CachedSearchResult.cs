namespace Arbitarr.Core.Caching;

/// <summary>
/// Persistence-agnostic snapshot of one search-result cache entry, as read from or written to
/// an <see cref="ISearchResultCacheStore"/>. Deliberately shaped to map cleanly onto
/// <c>Arbitarr.Data.Entities.SearchResultCacheEntry</c> without <see cref="Arbitarr.Core"/>
/// depending on Arbitarr.Data (AC6).
/// </summary>
/// <param name="QueryKey">
/// The resolved-identity cache key (season/episode/category/profile where available), never the
/// raw query string (AC23b(4)/M3-9) — callers are responsible for resolving this key before
/// calling into <see cref="SearchResultCache"/>.
/// </param>
/// <param name="PayloadJson">The serialized release set.</param>
/// <param name="FetchedAt">When this entry was last (re)populated from an upstream source.</param>
/// <param name="FreshUntil">Age boundary below which the entry is served with zero upstream calls.</param>
/// <param name="ServeUntil">Outer age boundary past which the entry is not served at all.</param>
/// <param name="LastRequestedAt">
/// When this entry was last actually served (Fresh or Stale-but-valid band). Never stamped for an
/// Expired-band request, since nothing is served there (Architect A1, M3-8a).
/// </param>
public sealed record CachedSearchResult(
    string QueryKey,
    string PayloadJson,
    DateTimeOffset FetchedAt,
    DateTimeOffset FreshUntil,
    DateTimeOffset ServeUntil,
    DateTimeOffset LastRequestedAt);
