using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// An audit record of a release being suppressed by the filtering pipeline.
/// </summary>
/// <param name="Release">Identity of the suppressed release.</param>
/// <param name="Reason">Human-readable reason for the suppression.</param>
/// <param name="SuppressedAt">When the suppression occurred.</param>
public sealed record SuppressionRecord(
    ReleaseIdentity Release,
    string Reason,
    DateTimeOffset SuppressedAt);
