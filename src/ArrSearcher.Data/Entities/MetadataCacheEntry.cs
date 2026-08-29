namespace ArrSearcher.Data.Entities;

/// <summary>
/// Cached identity/metadata (e.g. XEM/AniDB numbering) for a series, versioned against the
/// upstream source's own snapshot version so staleness can be detected per AC-M8 without
/// re-fetching on every read.
/// </summary>
public sealed class MetadataCacheEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Stable identity key this entry describes (e.g. TVDB id, series slug).</summary>
    public required string SeriesKey { get; set; }

    /// <summary>Name of the upstream metadata source this entry was resolved from (e.g. "xem", "anidb").</summary>
    public required string Source { get; set; }

    /// <summary>Serialized (JSON) payload of the resolved identity/numbering data.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>
    /// The upstream source's own snapshot/version marker at the time this entry was fetched.
    /// A change in this value (as observed on a later poll) means the cached payload is stale
    /// and must be refreshed, independent of wall-clock age (AC-M8).
    /// </summary>
    public required string SourceSnapshotVersion { get; set; }

    /// <summary>True if this is a negative cache entry (e.g. "no XEM coverage for this series").</summary>
    public bool IsNegative { get; set; }

    /// <summary>When this entry was last fetched/refreshed from the upstream source.</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>When this entry should next be considered for a refresh check.</summary>
    public DateTimeOffset RefreshAfter { get; set; }
}
