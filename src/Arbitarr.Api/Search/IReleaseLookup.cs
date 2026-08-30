using Arbitarr.Api.Rendering;

namespace Arbitarr.Api.Search;

/// <summary>
/// Resolves a previously rendered <see cref="ReleaseGuid"/> back to its
/// <see cref="RenderedRelease"/> (source name + upstream release), so
/// <see cref="DownloadProxyEndpoint"/> can locate the upstream download link without ever
/// touching a database on the hot download-proxy path. The pagination-snapshot cache (M1
/// step 3) is the production implementation: it already holds the full merged set keyed by
/// snapshot token, so guid resolution is a pure in-memory/deserialized lookup against the
/// most recent snapshot(s), not a new persistence concern.
/// </summary>
public interface IReleaseLookup
{
    /// <summary>Finds the rendered release whose <see cref="RenderedRelease.ProxyGuid"/> equals <paramref name="proxyGuid"/>, if still known.</summary>
    Task<RenderedRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default);
}
