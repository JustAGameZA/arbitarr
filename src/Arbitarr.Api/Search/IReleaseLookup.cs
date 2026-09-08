using Arbitarr.Api.Rendering;

namespace Arbitarr.Api.Search;

/// <summary>
/// Resolves a previously rendered <see cref="ReleaseGuid"/> back to its
/// <see cref="RenderedRelease"/> (source name + upstream release), so
/// <see cref="DownloadProxyEndpoint"/> can locate the upstream download link.
/// </summary>
public interface IReleaseLookup
{
    /// <summary>Finds the rendered release whose <see cref="RenderedRelease.ProxyGuid"/> equals <paramref name="proxyGuid"/>, if still known.</summary>
    Task<RenderedRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores the post-filter releases that may be resolved through the download proxy.
/// </summary>
public interface IProxyGuidReleaseRegistry : IReleaseLookup
{
    /// <summary>Records releases with the registry's fixed retention period.</summary>
    Task RecordRangeAsync(IEnumerable<RenderedRelease> releases, CancellationToken cancellationToken = default);
}
