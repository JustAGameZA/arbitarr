using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Pairs a merged <see cref="ReleaseCandidate"/> with the upstream source name that produced
/// it, so the renderer can compute a stable <see cref="ReleaseGuid"/> (source name + upstream
/// guid) without the pipeline needing to thread source identity through the candidate itself.
/// </summary>
/// <param name="SourceName">Name of the upstream source that produced this release.</param>
/// <param name="Candidate">The release payload, rendered byte-exact (pure passthrough).</param>
public sealed record RenderedRelease(string SourceName, ReleaseCandidate Candidate)
{
    /// <summary>The stable proxy guid for this release, used by DownloadProxyEndpoint.</summary>
    public string ProxyGuid => ReleaseGuid.Compute(new ReleaseIdentity(SourceName, Candidate.Guid));
}
