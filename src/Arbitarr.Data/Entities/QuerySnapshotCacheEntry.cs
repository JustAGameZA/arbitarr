namespace Arbitarr.Data.Entities;

/// <summary>
/// Short-TTL, pagination-scoped snapshot of a query's result set, so that repeated paging
/// requests against the same client session see a stable ordering rather than re-running a
/// live upstream fan-out per page.
/// </summary>
public sealed class QuerySnapshotCacheEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Opaque token identifying this paging session. Note: this is purely an internal cache key —
    /// a deterministic hash of the query's identity (search type + query text + categories; see
    /// <see cref="Arbitarr.Api.Search.PaginationSnapshotService"/>'s <c>ComputeSnapshotToken</c>).
    /// It is never returned to or accepted from Torznab/Newznab clients, unlike some indexer
    /// protocols' client-visible pagination cursors — callers simply repeat <c>offset</c>/<c>limit</c>
    /// against the same <c>t</c>/<c>q</c>/<c>cat</c>, and this service re-derives the same token to
    /// find the same snapshot row. Don't confuse it with a client-facing continuation token.
    /// </summary>
    public required string SnapshotToken { get; set; }

    /// <summary>Serialized (JSON) payload of the full ordered result set for this snapshot.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>When this snapshot was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Timestamp past which this snapshot is no longer valid (short TTL; see <c>query_snapshot_ttl</c> setting).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
