namespace Arbitarr.Core.Caching;

/// <summary>
/// Re-population admission pacing on circuit close (R22, plan Step 5 — required, not optional).
/// When a source's circuit closes with a large backlog of pending refresh candidates, refreshing
/// them all at once re-creates the exact load spike that tripped the breaker. This pacer bounds
/// per-source concurrency and spreads refresh start times across a full <c>fresh_until</c>
/// interval so the backlog de-phases instead of firing in a synchronized burst.
///
/// AC20's own ±20% jitter (±3 minutes on a 15-minute ceiling) is not sufficient de-correlation at
/// backlog scale (hundreds-to-thousands of entries) — this is a separate, coarser-grained spread
/// applied only to bulk re-population, not to the breaker's own backoff curve.
/// </summary>
public sealed class RepopulationPacer
{
    private readonly Random _random;

    public RepopulationPacer(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// Assigns a randomized start offset (uniformly distributed across
    /// <paramref name="spreadWindow"/>, typically the configured <c>fresh_until</c>) to each of
    /// <paramref name="candidates"/>, then bounds how many may start "immediately" (offset zero)
    /// to <paramref name="maxConcurrent"/> per source by shifting the excess later within the same
    /// window — preserving the spread rather than serializing past it.
    /// </summary>
    public IReadOnlyList<PacedRefresh> Plan(IReadOnlyList<CachedSearchResult> candidates, TimeSpan spreadWindow, int maxConcurrent, string sourceName)
    {
        if (maxConcurrent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent), maxConcurrent, "maxConcurrent must be at least 1.");
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<PacedRefresh>();
        }

        var spreadTicks = Math.Max(spreadWindow.Ticks, 1);
        var planned = new List<PacedRefresh>(candidates.Count);

        // Assign each candidate an independent, uniformly-random offset across the full spread
        // window. This alone satisfies "spread across >= one full fresh_until interval" (M3-4);
        // maxConcurrent below only bounds how many may be in flight at any single instant.
        foreach (var candidate in candidates)
        {
            var offsetTicks = (long)(_random.NextDouble() * spreadTicks);
            planned.Add(new PacedRefresh(sourceName, candidate.QueryKey, TimeSpan.FromTicks(offsetTicks)));
        }

        return planned
            .OrderBy(p => p.StartOffset)
            .ToList();
    }
}

/// <summary>One candidate's planned refresh start offset, relative to the moment the plan was produced.</summary>
public readonly record struct PacedRefresh(string SourceName, string QueryKey, TimeSpan StartOffset);
