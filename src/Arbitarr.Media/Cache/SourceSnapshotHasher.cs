using System.Security.Cryptography;
using System.Text;

namespace Arbitarr.Media.Cache;

/// <summary>
/// Computes a stable content hash for a fetched upstream metadata payload, used as the
/// <c>SourceSnapshotVersion</c> stamp on <see cref="Arbitarr.Data.Entities.MetadataCacheEntry"/>
/// (AC-M8).
/// </summary>
/// <remarks>
/// XEM's map data is hand-edited with no changelog and no reliable HTTP freshness header (plan
/// risk R7) — an <c>ETag</c>/<c>Last-Modified</c> value, even if present, cannot be trusted to
/// reflect an actual content change. Snapshot-hashing the fetched body itself is the only signal
/// that survives that: two fetches with identical hashes are guaranteed to carry identical content
/// regardless of what any freshness header claimed, and a changed hash is the sole trigger for
/// invalidating a cached entry.
/// </remarks>
public static class SourceSnapshotHasher
{
    /// <summary>
    /// Computes a stable hex-encoded SHA-256 digest of the given raw content, suitable for storing
    /// as <c>SourceSnapshotVersion</c> and comparing across fetches to detect upstream change.
    /// </summary>
    /// <param name="rawContent">The raw fetched content (e.g. JSON/XML response body) to hash.</param>
    public static string ComputeHash(string rawContent)
    {
        ArgumentNullException.ThrowIfNull(rawContent);

        var bytes = Encoding.UTF8.GetBytes(rawContent);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
