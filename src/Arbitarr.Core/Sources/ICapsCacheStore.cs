namespace ArrSearcher.Core.Sources;

/// <summary>
/// Persistence-agnostic last-known-good cache for per-source <see cref="SourceCaps"/>. Backs
/// <see cref="CapsAggregator"/>'s fallback behaviour: if fetching caps from an upstream fails,
/// the aggregator falls back to the most recently successfully cached caps for that source
/// rather than dropping it from the merge or returning empty/default caps.
///
/// This interface is deliberately persistence-agnostic — the EF Core-backed implementation
/// against ArrSearcher.Data's CapsCacheEntry table is wired up separately, outside Core, to
/// keep Core free of references to ArrSearcher.Data (AC6).
/// </summary>
public interface ICapsCacheStore
{
    /// <summary>Retrieves the most recently successfully cached caps for a source, if any.</summary>
    Task<SourceCaps?> GetLastKnownGoodAsync(string sourceName, CancellationToken cancellationToken = default);

    /// <summary>Persists a successfully fetched caps result for a source as the new last-known-good value.</summary>
    Task SaveAsync(string sourceName, SourceCaps caps, CancellationToken cancellationToken = default);
}
