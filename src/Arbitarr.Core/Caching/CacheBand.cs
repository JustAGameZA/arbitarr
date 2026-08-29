namespace Arbitarr.Core.Caching;

/// <summary>
/// Which of the two-age cache's three bands a search-result cache entry currently falls into,
/// relative to <c>now</c> (plan Step 4a). Bands are evaluated strictly at read time from
/// <c>FreshUntil</c>/<c>ServeUntil</c>; nothing about band membership is persisted.
/// </summary>
public enum CacheBand
{
    /// <summary>
    /// <c>now &lt; FreshUntil</c>. Served directly with zero upstream requests (AC23b).
    /// </summary>
    Fresh,

    /// <summary>
    /// <c>FreshUntil &lt;= now &lt; ServeUntil</c>. Served immediately; if the proactive worker has
    /// not already refreshed the entry, a secondary live upstream attempt is made (bounded by its
    /// slice of the search budget) and the cached set is used only if that attempt fails or times
    /// out (AC23/AC23c).
    /// </summary>
    StaleButValid,

    /// <summary>
    /// <c>now &gt;= ServeUntil</c>. Not served at all: a live attempt is made and, on failure, a
    /// flagged partial/empty result is returned instead of the cached set (AC23).
    /// </summary>
    Expired,
}
