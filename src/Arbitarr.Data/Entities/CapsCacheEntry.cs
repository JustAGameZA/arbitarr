namespace ArrSearcher.Data.Entities;

/// <summary>
/// Last-known-good capability info for a single upstream source, so a dead upstream cannot
/// narrow the caps advertised to *arr. Aggregation (union/intersection across sources) is
/// computed by the consumer (Step 2a); this entity persists the per-source raw caps only.
/// </summary>
public sealed class CapsCacheEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Name/identifier of the upstream source these caps describe.</summary>
    public required string SourceName { get; set; }

    /// <summary>Serialized (JSON) payload of the source's raw advertised caps.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>When these caps were last successfully fetched from the source.</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>True if this row is the last-known-good value from a source currently unreachable.</summary>
    public bool IsStale { get; set; }
}
