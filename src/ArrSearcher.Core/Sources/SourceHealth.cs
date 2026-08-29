namespace ArrSearcher.Core.Sources;

/// <summary>
/// Point-in-time health/availability state for an upstream source, e.g. as tracked by a
/// per-source circuit breaker.
/// </summary>
public enum SourceHealthState
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unavailable = 3,
}

/// <summary>
/// Snapshot of an upstream source's current health.
/// </summary>
/// <param name="State">The current health state.</param>
/// <param name="LastCheckedAt">When this snapshot was produced.</param>
/// <param name="LastError">The most recent error message, if any.</param>
public sealed record SourceHealth(
    SourceHealthState State,
    DateTimeOffset LastCheckedAt,
    string? LastError);
