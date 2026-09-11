using Arbitarr.Api.Rendering;

namespace Arbitarr.Api.Search;

/// <summary>
/// Resolves a previously rendered <see cref="ReleaseGuid"/> back to its
/// <see cref="RenderedRelease"/> (source name + upstream release), so
/// <see cref="DownloadProxyEndpoint"/> can locate the upstream download link.
///
/// <para>arb-tps: the production implementation is <see cref="PersistentReleaseLookup"/>, which is
/// TWO TIERS — <see cref="InMemoryReleaseLookup"/> for the hot path, and a durable
/// <see cref="Arbitarr.Core.Releases.IReleaseLookupStore"/> row behind it. The hot path still
/// touches no database, which is what that zero-DB intent was protecting; but "never touching a
/// database" was a stronger promise than the endpoint could keep, because a lookup that lived only
/// in process memory answered 404 on every link after a restart and after 30 minutes. The database
/// is now reached only on a memory miss, where the alternative is a failed download rather than a
/// faster one.</para>
///
/// <para>The pagination-snapshot cache was once expected to become that durable implementation.
/// It could not: neither it nor the search-result cache is keyed by proxy guid, so resolving one
/// would mean scanning rows and recomputing HMACs on the download path.</para>
/// </summary>
public interface IReleaseLookup
{
    /// <summary>Finds the rendered release whose <see cref="RenderedRelease.ProxyGuid"/> equals <paramref name="proxyGuid"/>, if still known.</summary>
    Task<RenderedRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default);
}
