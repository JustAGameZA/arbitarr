namespace Arbitarr.Core.Sources;

/// <summary>
/// Persistence-agnostic contract for the pagination-snapshot cache (M1 step 3, AC16). A snapshot
/// materializes the full merged result set for a given query identity — <c>t</c> (search type)
/// plus <c>q</c> plus <c>cat</c>, deliberately excluding <c>offset</c>/<c>limit</c> — so that
/// successive paged requests against "the same query" are served consistent, disjoint,
/// union-complete slices even if the upstream source set mutates between calls.
///
/// This interface is deliberately payload-shape-agnostic (a caller-supplied JSON string) so it
/// can live in Arbitarr.Core without Core taking a reference to Arbitarr.Api's rendering types
/// (AC6) — mirrors the <see cref="ICapsCacheStore"/> persistence-agnostic-contract pattern, one
/// layer down: the Api layer serializes/deserializes its own <c>RenderedRelease</c> payload and
/// only ever hands this store an opaque JSON blob plus a deterministic key.
/// </summary>
public interface IQuerySnapshotStore
{
    /// <summary>
    /// Returns the still-valid (not expired as of <paramref name="asOf"/>) snapshot payload for
    /// <paramref name="snapshotToken"/>, or null if no such snapshot exists or it has expired.
    /// </summary>
    Task<string?> GetAsync(string snapshotToken, DateTimeOffset asOf, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the snapshot payload for <paramref name="snapshotToken"/>, setting its expiry to
    /// <paramref name="createdAt"/> + <paramref name="ttl"/>.
    /// </summary>
    Task SaveAsync(string snapshotToken, string payloadJson, DateTimeOffset createdAt, TimeSpan ttl, CancellationToken cancellationToken = default);
}
