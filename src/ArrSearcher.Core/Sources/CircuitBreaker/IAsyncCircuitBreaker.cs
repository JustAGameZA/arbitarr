namespace ArrSearcher.Core.Sources.CircuitBreaker;

/// <summary>
/// Per-source circuit breaker abstraction consulted by upstream source adapters (e.g.
/// <c>NzbHydraSource</c>) before issuing a call and reported back into after each call completes.
///
/// <para>
/// Async because the production implementation (<c>ArrSearcher.Data.CircuitBreaker.PersistentSourceCircuitBreaker</c>)
/// hydrates from a persisted <c>SourceHealthRecord</c> row on first use per source, which is
/// necessarily an I/O-bound operation. The underlying decision logic (AC20's open/half-open/closed
/// state machine, backoff/jitter curve) lives in the synchronous, dependency-free
/// <see cref="SourceCircuitBreaker"/> — this interface exists only so adapter code can depend on
/// the async, persistence-backed shape without referencing ArrSearcher.Data directly (keeping AC6
/// isolation intact: Core defines the contract, Data supplies the implementation).
/// </para>
/// </summary>
public interface IAsyncCircuitBreaker
{
    /// <summary>
    /// Returns whether a call to <paramref name="sourceName"/> is currently permitted (i.e. the
    /// breaker for that source is not open). Callers must check this before issuing an upstream
    /// call and short-circuit without calling out when it returns <c>false</c>.
    /// </summary>
    Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default);

    /// <summary>Records a successful upstream call for <paramref name="sourceName"/>.</summary>
    Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default);

    /// <summary>Records a failed upstream call for <paramref name="sourceName"/>, with the triggering exception.</summary>
    Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default);
}
