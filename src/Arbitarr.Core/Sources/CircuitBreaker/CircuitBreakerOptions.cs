namespace Arbitarr.Core.Sources.CircuitBreaker;

/// <summary>
/// Overridable constants for <see cref="SourceCircuitBreaker"/>, defaulting to AC20's curve as
/// carried forward (unvalidated against live faults prior to this build) in
/// <c>docs/step0-measurements.md</c> ("Circuit breaker constants" table near the end): 3
/// consecutive failures to open, 5s initial backoff doubling to a 15-minute ceiling, ±20% jitter,
/// a 5-minute probe interval while open, and 1 success to close.
///
/// <para>
/// These are kept as local, overridable options rather than hardcoded magic numbers scattered
/// through the state machine, per this task's scope. They are NOT part of the Step 2
/// <c>SettingsSnapshot</c>/<c>SettingsValidator</c> catalog — that catalog is considered locked in
/// from Step 2's verified state and this worker was directed not to add to it. Flagged here as a
/// candidate for a future settings-catalog addition; the lead should decide whether/when these
/// constants should become user-overridable settings alongside Step 2's other 13 keys.
/// </para>
/// </summary>
public sealed record CircuitBreakerOptions
{
    /// <summary>Number of consecutive failures required to trip Closed -&gt; Open. AC20 default: 3.</summary>
    public int ConsecutiveFailuresToOpen { get; init; } = 3;

    /// <summary>Backoff duration applied the first time the breaker opens. AC20 default: 5s.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling the doubling backoff curve never exceeds. AC20 default: 15 minutes.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Fractional jitter applied symmetrically to the computed backoff (0.2 == ±20%). AC20 default: 0.2.</summary>
    public double JitterFraction { get; init; } = 0.2;

    /// <summary>
    /// How long the breaker waits, from when it most recently opened, before allowing a single
    /// half-open probe call through. Distinct from <see cref="InitialBackoff"/>/<see cref="MaxBackoff"/>
    /// doubling: the probe interval is the fixed cadence at which retries are *attempted* while
    /// open; the backoff curve independently tracks how "unhealthy" the source is judged to be and
    /// grows on repeated failed probes, per AC20's curve. AC20 default: 5 minutes.
    /// </summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Number of consecutive successes in Half-Open required to close the breaker. AC20 default: 1.</summary>
    public int SuccessesToClose { get; init; } = 1;

    /// <summary>AC20's default curve, unvalidated against live faults prior to this build (docs/step0-measurements.md §5).</summary>
    public static CircuitBreakerOptions Default { get; } = new();
}
