using System.Collections.Concurrent;

namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// One recorded search, as surfaced by the M2 dashboard's recent-searches panel.
/// </summary>
/// <param name="ReceivedAt">When the search request was received.</param>
/// <param name="Query">The raw query string received from the *arr client.</param>
/// <param name="ResolvedIdentity">
/// The identity resolved for this query (e.g. series/movie title), or <c>null</c> if identity
/// resolution has not been wired up yet or did not resolve.
/// </param>
/// <param name="ResultCount">Number of results returned to the client.</param>
/// <param name="ElapsedMilliseconds">Wall-clock time taken to serve the request, in milliseconds.</param>
/// <param name="Band">The cache band the response was served from (e.g. "fresh", "stale", "expired"), or <c>null</c> before M3's cache lands.</param>
public sealed record RecentSearchEntry(
    DateTimeOffset ReceivedAt,
    string Query,
    string? ResolvedIdentity,
    int ResultCount,
    double ElapsedMilliseconds,
    string? Band);

/// <summary>
/// Bounded in-memory ring buffer of recent searches, for the read-only dashboard's
/// recent-searches panel (D1). Deliberately not persisted: this is diagnostic-only, and adding a
/// write-heavy table on the search critical path would work against R18/R19. Entries are lost on
/// restart, which is an accepted tradeoff for a "what just happened" view.
/// </summary>
public sealed class RecentSearchLog
{
    /// <summary>Default ring-buffer capacity per the plan (M2 §3).</summary>
    public const int DefaultCapacity = 200;

    private readonly ConcurrentQueue<RecentSearchEntry> _entries = new();
    private readonly int _capacity;

    public RecentSearchLog(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _capacity = capacity;
    }

    /// <summary>Records a completed search, evicting the oldest entry if the buffer is at capacity.</summary>
    public void Record(RecentSearchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries.Enqueue(entry);

        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
            // Drain down to capacity.
        }
    }

    /// <summary>
    /// Returns recorded entries, most-recent-first, oldest already evicted per the ring-buffer
    /// capacity.
    /// </summary>
    public IReadOnlyList<RecentSearchEntry> GetRecent() => _entries.Reverse().ToArray();
}
