namespace Arbitarr.Core.Releases;

/// <summary>
/// Identifies a single release/result within a given upstream source, independent of
/// the media-identity resolution performed by Arbitarr.Media (Core.Identity).
/// </summary>
/// <param name="SourceName">The name of the upstream source that produced this release.</param>
/// <param name="Guid">The source-provided unique identifier (Torznab/Newznab &lt;guid&gt;).</param>
public readonly record struct ReleaseIdentity(string SourceName, string Guid);
