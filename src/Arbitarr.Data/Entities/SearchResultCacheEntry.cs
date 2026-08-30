namespace Arbitarr.Data.Entities;

/// <summary>
/// Cached merged search-result payload for a given upstream query, keyed so identical queries
/// can be served without a fresh upstream fan-out while within <see cref="FreshUntil"/>, and
/// used as an availability fallback until <see cref="ServeUntil"/> (Step 4a's proactive worker
/// owns keeping entries inside the fresh band; this entity is schema-only).
/// </summary>
public sealed class SearchResultCacheEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Normalized cache key identifying the upstream query (query params, source set, paging).</summary>
    public required string QueryKey { get; set; }

    /// <summary>Serialized (JSON) payload of the merged <c>ReleaseCandidate</c> results.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>When this entry was produced (last successful upstream fetch).</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>
    /// Timestamp until which this entry may be served directly with zero upstream requests
    /// (the "Fresh" band). Compared against the configured <c>fresh_until</c> setting.
    /// </summary>
    public DateTimeOffset FreshUntil { get; set; }

    /// <summary>
    /// Outer timestamp past which this entry must not be served at all (the availability-fallback
    /// bound). Compared against the configured <c>serve_until</c> setting. The maintenance job's
    /// prune predicate for this table is strictly "row age past ServeUntil" — see plan Step 2.
    /// </summary>
    public DateTimeOffset ServeUntil { get; set; }

    /// <summary>
    /// When this entry was last actually served to a caller (Fresh or Stale-but-valid band),
    /// used to scope the proactive worker's <c>active_window</c> selection predicate. This is
    /// stamped only on a serve, never on mere request arrival, and MUST NOT be stamped when a
    /// request falls in the Expired band (nothing is served there) — otherwise dead queries would
    /// stay inside <c>active_window</c> forever and the proactive worker would keep refreshing
    /// entries nobody consumes, defeating the "keep a sick upstream off the critical path" goal.
    /// </summary>
    public DateTimeOffset LastRequestedAt { get; set; }
}
