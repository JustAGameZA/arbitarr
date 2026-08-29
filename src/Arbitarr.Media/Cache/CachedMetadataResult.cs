using ArrSearcher.Core.Identity;

namespace ArrSearcher.Media.Cache;

/// <summary>
/// Result of a <see cref="MetadataCacheCoordinator"/> resolution, carrying the resolved payload (if
/// any) together with the exact <see cref="MatchProvenanceFlags"/> bits AC-M6 requires the three
/// degraded states to be reported through — the same additive channel <c>MatchProvenance</c> uses,
/// not a private enum that duplicates it.
/// </summary>
/// <param name="PayloadJson">
/// The resolved payload, present when a positive cache entry or a fresh fetch succeeded. Null for
/// cache-absent-and-unreachable, source-unreachable, and no-xem-coverage outcomes.
/// </param>
/// <param name="SourceSnapshotVersion">The snapshot hash backing this result, if any payload/negative state was resolved.</param>
/// <param name="Flags">
/// <see cref="MatchProvenanceFlags.None"/> on a clean positive resolution; otherwise the combination
/// of <see cref="MatchProvenanceFlags.CacheAbsent"/>, <see cref="MatchProvenanceFlags.SourceUnreachable"/>,
/// and/or <see cref="MatchProvenanceFlags.NoXemCoverage"/> describing what degraded the resolution.
/// </param>
public sealed record CachedMetadataResult(
    string? PayloadJson,
    string? SourceSnapshotVersion,
    MatchProvenanceFlags Flags)
{
    public static CachedMetadataResult Success(string payloadJson, string sourceSnapshotVersion) =>
        new(payloadJson, sourceSnapshotVersion, MatchProvenanceFlags.None);
}
