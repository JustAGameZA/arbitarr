namespace Arbitarr.Data.Entities;

/// <summary>
/// Per-source circuit breaker state. Named distinctly from
/// <c>Arbitarr.Core.Sources.SourceHealth</c> (the in-memory point-in-time snapshot type) since
/// this is the persisted breaker state row. Default curve values (consecutive failures to open,
/// backoff growth/ceiling, jitter, probe interval) come from <c>docs/step0-measurements.md</c> and
/// are seeded/configured by worker-2's runtime logic, not hardcoded into this entity's shape.
/// </summary>
public sealed class SourceHealthRecord
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Name/identifier of the upstream source this breaker state tracks.</summary>
    public required string SourceName { get; set; }

    /// <summary>Current breaker state.</summary>
    public CircuitBreakerState State { get; set; }

    /// <summary>Consecutive failure count observed since the breaker last closed.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Current backoff duration in seconds (doubling curve, capped at a ceiling).</summary>
    public double CurrentBackoffSeconds { get; set; }

    /// <summary>Timestamp of the most recent failure, if any.</summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Timestamp of the most recent success, if any.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Most recent error message, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>When the breaker may next attempt a probe request while open/half-open.</summary>
    public DateTimeOffset? NextProbeAt { get; set; }
}

/// <summary>Circuit breaker lifecycle state for a source health record.</summary>
public enum CircuitBreakerState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
}
