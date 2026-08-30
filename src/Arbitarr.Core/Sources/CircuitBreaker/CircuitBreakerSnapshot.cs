namespace Arbitarr.Core.Sources.CircuitBreaker;

/// <summary>
/// Point-in-time state for one source's circuit breaker, as tracked by
/// <see cref="SourceCircuitBreaker"/>. Deliberately shaped to map cleanly onto
/// <c>Arbitarr.Data.Entities.SourceHealthRecord</c> so the persistence adapter is a thin
/// translation, but this type itself has no dependency on Arbitarr.Data.
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="ConsecutiveFailures">Consecutive failure count observed since the breaker last closed.</param>
/// <param name="CurrentBackoff">
/// Current backoff duration: the doubling curve's base value (capped at
/// <see cref="CircuitBreakerOptions.MaxBackoff"/>) with jitter already applied. This is also the
/// open-duration gate — <see cref="NextProbeAt"/> is set to the time the breaker opened plus this
/// value (M3-12).
/// </param>
/// <param name="LastFailureAt">Timestamp of the most recent failure, if any.</param>
/// <param name="LastSuccessAt">Timestamp of the most recent success, if any.</param>
/// <param name="LastError">Most recent error message, if any.</param>
/// <param name="NextProbeAt">When the breaker may next attempt a probe call while Open. Null when Closed.</param>
public sealed record CircuitBreakerSnapshot(
    CircuitState State,
    int ConsecutiveFailures,
    TimeSpan CurrentBackoff,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    DateTimeOffset? NextProbeAt)
{
    /// <summary>The initial state for a source that has never recorded a call: Closed, zero failures, no backoff.</summary>
    public static CircuitBreakerSnapshot Initial { get; } = new(
        State: CircuitState.Closed,
        ConsecutiveFailures: 0,
        CurrentBackoff: TimeSpan.Zero,
        LastFailureAt: null,
        LastSuccessAt: null,
        LastError: null,
        NextProbeAt: null);
}
