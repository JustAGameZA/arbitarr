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
/// </summary>
public sealed class InMemoryReleaseLookup : IReleaseLookup
{
    private readonly ConcurrentDictionary<string, RenderedRelease> _releases = new(StringComparer.Ordinal);

    public void Record(RenderedRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        _releases[release.ProxyGuid] = release;
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
        _releases.TryGetValue(proxyGuid, out var release);
        return Task.FromResult(release);
    }
}
