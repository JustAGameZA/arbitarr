namespace ArrSearcher.Core.Sources.CircuitBreaker;

/// <summary>
/// Lifecycle state of a per-source circuit breaker (AC20). Mirrors
/// <c>ArrSearcher.Data.Entities.CircuitBreakerState</c> in shape/intent, but this enum is defined
/// here (in Core) so the pure state machine stays free of any dependency on ArrSearcher.Data.
/// The persistence adapter translates between the two.
/// </summary>
public enum CircuitState
{
    /// <summary>Healthy — calls are allowed.</summary>
    Closed = 0,

    /// <summary>Tripped after too many consecutive failures — calls are refused until the probe interval elapses.</summary>
    Open = 1,

    /// <summary>Probe interval elapsed — exactly one trial call is allowed through to decide Closed vs. Open.</summary>
    HalfOpen = 2,
}
