namespace Arbitarr.Core.Sources;

/// <summary>
/// Persistence-agnostic last-known-good cache for per-source <see cref="SourceCaps"/>. Backs
/// <see cref="CapsAggregator"/>'s fallback behaviour: if fetching caps from an upstream fails,
/// the aggregator falls back to the most recently successfully cached caps for that source
/// rather than dropping it from the merge or returning empty/default caps.
///
/// This interface is deliberately persistence-agnostic — the EF Core-backed implementation
/// against Arbitarr.Data's CapsCacheEntry table is wired up separately, outside Core, to
/// keep Core free of references to Arbitarr.Data (AC6).
///
/// <para><b>WHAT IS STORED IS RAW UPSTREAM CAPS, BOOK CATEGORIES INCLUDED.</b> Every writer here
/// (<see cref="CapsAggregator"/>'s fetch path and <see cref="CapsRefresher"/>) saves exactly what
/// the upstream answered, unfiltered. The unconditional book-category exclusion lives one layer up,
/// in <see cref="CapsAggregator"/>'s merge, and is applied on the way OUT. That split is deliberate:
/// filtering on the way in would bake the current policy into rows that outlive it, so a later
/// change to what is excluded could not take effect until every entry had been re-fetched.
///
/// The consequence is a rule with no exception: <b>no UI, endpoint or other caller may read this
/// store directly to render or advertise categories.</b> Doing so bypasses the exclusion and
/// advertises book categories downstream — the exact outcome the exclusion exists to prevent.
/// Categories are served only through the aggregator's merged answer.</para>
/// </summary>
public interface ICapsCacheStore
{
    /// <summary>Retrieves the most recently successfully cached caps for a source, if any.</summary>
    Task<SourceCaps?> GetLastKnownGoodAsync(string sourceName, CancellationToken cancellationToken = default);

    /// <summary>Persists a successfully fetched caps result for a source as the new last-known-good value.</summary>
    Task SaveAsync(string sourceName, SourceCaps caps, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every stored entry for <paramref name="sourceName"/> — both protocol families' keys
    /// (<see cref="CapsAggregator.CacheKey"/>) — leaving every other source's entries untouched.
    /// Takes the BARE source name rather than a cache key, precisely because it spans the several
    /// keys that one name expands into.
    ///
    /// <para><b>Why this exists (ADR 0016's set-membership rule).</b> A cached entry's only reason
    /// to exist is the configured source it describes. When that reason ends — the row is deleted,
    /// or renamed so the old name describes nothing — the entry must go with it. Left behind it is
    /// not merely dead weight in a backup: the key is the display name, so a later source created
    /// or renamed to that same name would silently ADOPT a stale predecessor's categories as its
    /// own last-known-good and serve them from its first upstream failure onward.</para>
    ///
    /// <para>Deleting an absent entry is not an error. Callers delete before a refresh without
    /// first establishing that anything was ever stored.</para>
    /// </summary>
    Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default);
}
