namespace Arbitarr.Data.Entities;

/// <summary>
/// A short-lived, durable mapping from a download proxy guid to the upstream source and release
/// candidate needed to fetch its payload. This is intentionally not exposed through admin APIs.
/// </summary>
public sealed class ProxyGuidReleaseEntry
{
    public required string ProxyGuid { get; set; }

    public required string SourceName { get; set; }

    public required string CandidateJson { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}