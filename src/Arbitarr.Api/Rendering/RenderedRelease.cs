using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Pairs a merged <see cref="ReleaseCandidate"/> with the upstream source name that produced
/// it, so the renderer can compute a stable <see cref="ReleaseGuid"/> (source name + upstream
/// guid) without the pipeline needing to thread source identity through the candidate itself.
/// </summary>
/// <param name="SourceName">Name of the upstream source that produced this release.</param>
/// <param name="Candidate">The release payload, rendered byte-exact (pure passthrough).</param>
/// <param name="SuppressionAnnotation">
/// Additive metadata only (M4-7) — never mutates <paramref name="Candidate"/>'s title, size,
/// category, or guid (M1-4). Null for a release that was never suppressed. When shadow mode
/// re-admits a would-be-suppressed release, this carries a human-readable reason so the response
/// still shows the release was matched by a suppression source, just not enforced.
/// </param>
public sealed record RenderedRelease(string SourceName, ReleaseCandidate Candidate, string? SuppressionAnnotation = null)
{
    /// <summary>The stable proxy guid for this release, used by DownloadProxyEndpoint.</summary>
    public string ProxyGuid => ReleaseGuid.Compute(new ReleaseIdentity(SourceName, Candidate.Guid));
}
