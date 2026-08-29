namespace Arbitarr.Media.Cache;

/// <summary>
/// Outcome of a <see cref="IMetadataCacheStore"/> lookup, distinguishing the states AC-M6 requires
/// to be recorded distinctly rather than collapsed into one generic failure shape.
/// </summary>
public enum MetadataCacheLookupKind
{
    /// <summary>A positive, non-negative cache entry was found and returned.</summary>
    Hit,

    /// <summary>
    /// No cache row exists for this key at all (cache-absent). Distinct from
    /// <see cref="NegativeHit"/>: an absent cache means "we don't know yet", not "we confirmed
    /// there is nothing".
    /// </summary>
    Absent,

    /// <summary>
    /// A negative cache row exists (AC-M6: "XEM has no map for this series" is itself a cacheable
    /// fact). Distinct from <see cref="Absent"/> — this is a confirmed no-coverage result, not an
    /// unknown one, and callers should not re-hit the upstream for it until <c>RefreshAfter</c>.
    /// </summary>
    NegativeHit,
}

/// <summary>
/// Result of looking up a cached metadata payload for a series/source key.
/// </summary>
/// <param name="Kind">Which of the three distinct outcomes this lookup produced.</param>
/// <param name="PayloadJson">The cached payload, present only when <see cref="Kind"/> is <see cref="MetadataCacheLookupKind.Hit"/>.</param>
/// <param name="SourceSnapshotVersion">
/// The snapshot hash recorded for this entry, present for both <see cref="MetadataCacheLookupKind.Hit"/>
/// and <see cref="MetadataCacheLookupKind.NegativeHit"/> so a caller can compare it against a freshly
/// fetched hash to decide whether re-validation is even necessary.
/// </param>
/// <param name="RefreshAfter">When this entry should next be considered for a refresh check, if known.</param>
public sealed record MetadataCacheLookup(
    MetadataCacheLookupKind Kind,
    string? PayloadJson,
    string? SourceSnapshotVersion,
    DateTimeOffset? RefreshAfter)
{
    public static MetadataCacheLookup Absent() => new(MetadataCacheLookupKind.Absent, null, null, null);

    public static MetadataCacheLookup Hit(string payloadJson, string sourceSnapshotVersion, DateTimeOffset refreshAfter) =>
        new(MetadataCacheLookupKind.Hit, payloadJson, sourceSnapshotVersion, refreshAfter);

    public static MetadataCacheLookup NegativeHit(string sourceSnapshotVersion, DateTimeOffset refreshAfter) =>
        new(MetadataCacheLookupKind.NegativeHit, null, sourceSnapshotVersion, refreshAfter);
}
