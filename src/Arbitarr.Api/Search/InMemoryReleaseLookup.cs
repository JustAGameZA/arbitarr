using System.Collections.Concurrent;
using Arbitarr.Api.Rendering;

namespace Arbitarr.Api.Search;

/// <summary>
/// Process-lifetime, in-memory <see cref="IReleaseLookup"/>: <see cref="SearchEndpoint"/> records
/// every <see cref="RenderedRelease"/> it renders (keyed by <see cref="RenderedRelease.ProxyGuid"/>)
/// so <see cref="DownloadProxyEndpoint"/> can resolve it back to an upstream source/link without a
/// database round trip.
///
/// arb-tps: this is no longer the whole lookup — it is the FAST TIER of
/// <see cref="PersistentReleaseLookup"/>, which falls back to a durable
/// <see cref="Arbitarr.Core.Releases.IReleaseLookupStore"/> row when this dictionary misses, and
/// repopulates this tier from a store hit. It is deliberately still bounded by
/// <see cref="MaxEntries"/> and <see cref="EntryTtl"/>: those bounds are what keep a long-running
/// process's memory finite, and the durable tier is what makes them survivable rather than a source
/// of 404s on every link after a restart or after 30 minutes. DO NOT widen this type to consult the
/// database — the two-tier split is the design, and the classifier polling worker depends on
/// <see cref="Snapshot"/> meaning "what this process has recently rendered", not "every release ever
/// persisted".
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

    /// <summary>
    /// Tracks <paramref name="release"/> under its own <see cref="RenderedRelease.ProxyGuid"/> —
    /// the search path's entry point, where the guid was just derived from this very release.
    /// </summary>
    public void Record(RenderedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        RecordAs(release.ProxyGuid, release);
    }

    /// <summary>
    /// Tracks <paramref name="release"/> under an explicitly supplied <paramref name="proxyGuid"/>.
    ///
    /// <para>arb-tps: exists for <see cref="PersistentReleaseLookup"/>'s repopulation path, which
    /// holds the guid a durable row was STORED under and must file the entry under that rather than
    /// re-deriving one. The two agree while the release-guid secret is unchanged, but a rotation
    /// makes a re-derived guid differ from the one the caller is asking for — and an entry filed
    /// under a key nothing looks up is a fast path that silently never hits. Prefer
    /// <see cref="Record"/> wherever the release is the source of its own guid.</para>
    /// </summary>
    public void RecordAs(string proxyGuid, RenderedRelease release)
    {
        ArgumentNullException.ThrowIfNull(proxyGuid);
        ArgumentNullException.ThrowIfNull(release);

        var isNewKey = !_releases.ContainsKey(proxyGuid);
        _releases[proxyGuid] = new Entry(release, _timeProvider.GetUtcNow());

        if (isNewKey)
        {
            _insertionOrder.Enqueue(proxyGuid);
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
