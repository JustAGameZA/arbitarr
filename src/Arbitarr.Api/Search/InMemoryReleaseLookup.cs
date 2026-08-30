using System.Collections.Concurrent;
using Arbitarr.Api.Rendering;

namespace Arbitarr.Api.Search;

/// <summary>
/// Process-lifetime, in-memory <see cref="IReleaseLookup"/>: <see cref="SearchEndpoint"/> records
/// every <see cref="RenderedRelease"/> it renders (keyed by <see cref="RenderedRelease.ProxyGuid"/>)
/// so <see cref="DownloadProxyEndpoint"/> can resolve it back to an upstream source/link without a
/// database round trip. This is an interim Pass-A implementation; the pagination-snapshot cache
/// (M1 step 3) is expected to become the production-grade, TTL-bounded implementation, at which
/// point this type may be retired or kept only as an in-process fast path.
///
/// SEC-M3: without a bound, this dictionary grows without limit for the lifetime of the process —
/// every distinct release ever rendered across every search stays resident forever, which is an
/// unbounded-memory-growth vector for a long-running instance fielding many distinct queries. It
/// is now capped at <see cref="MaxEntries"/> entries; once at capacity, the oldest still-tracked
/// entry (by insertion order) is evicted before a new one is added, and entries additionally expire
/// after <see cref="EntryTtl"/> regardless of capacity pressure.
/// </summary>
public sealed class InMemoryReleaseLookup : IReleaseLookup
{
    /// <summary>Hard cap on the number of distinct proxy-guid entries retained at once.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>Maximum age of a tracked entry before it is treated as absent, independent of capacity pressure.</summary>
    public static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(30);

    private sealed record Entry(RenderedRelease Release, DateTimeOffset RecordedAt);

    private readonly ConcurrentDictionary<string, Entry> _releases = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryReleaseLookup()
        : this(TimeProvider.System)
    {
    }

    public InMemoryReleaseLookup(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void Record(RenderedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        var isNewKey = !_releases.ContainsKey(release.ProxyGuid);
        _releases[release.ProxyGuid] = new Entry(release, _timeProvider.GetUtcNow());

        if (isNewKey)
        {
            _insertionOrder.Enqueue(release.ProxyGuid);
            EvictWhileOverCapacity();
        }
    }

    public void RecordRange(IEnumerable<RenderedRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        foreach (var release in releases)
        {
            Record(release);
        }
    }

    public Task<RenderedRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default)
    {
        if (!_releases.TryGetValue(proxyGuid, out var entry))
        {
            return Task.FromResult<RenderedRelease?>(null);
        }

        if (_timeProvider.GetUtcNow() - entry.RecordedAt > EntryTtl)
        {
            _releases.TryRemove(proxyGuid, out _);
            return Task.FromResult<RenderedRelease?>(null);
        }

        return Task.FromResult<RenderedRelease?>(entry.Release);
    }

    /// <summary>
    /// Returns a point-in-time snapshot of every currently-tracked, non-expired release. Used by the
    /// classifier polling worker as its candidate source — the worker never talks to
    /// upstream sources directly, only to whatever this process has recently rendered.
    /// </summary>
    public IReadOnlyList<RenderedRelease> Snapshot()
    {
        var now = _timeProvider.GetUtcNow();
        var result = new List<RenderedRelease>(_releases.Count);
        foreach (var entry in _releases.Values)
        {
            if (now - entry.RecordedAt <= EntryTtl)
            {
                result.Add(entry.Release);
            }
        }

        return result;
    }

    private void EvictWhileOverCapacity()
    {
        while (_releases.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldestKey))
        {
            _releases.TryRemove(oldestKey, out _);
        }
    }
}
