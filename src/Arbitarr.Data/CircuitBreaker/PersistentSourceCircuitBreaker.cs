using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Data.CircuitBreaker;

/// <summary>
/// Thin composition wrapper that pairs a pure <see cref="SourceCircuitBreaker"/> with a
/// <see cref="SourceHealthRepository"/> so callers get persistence-across-restarts without the
/// state machine itself depending on SQLite/EF. All decision logic (open/close/backoff/jitter)
/// lives in <see cref="SourceCircuitBreaker"/> unchanged; this class only adds
/// load-on-first-use/save-after-mutation around it.
/// </summary>
public sealed class PersistentSourceCircuitBreaker : IAsyncCircuitBreaker
{
    private readonly SourceCircuitBreaker _breaker;
    private readonly SourceHealthRepository _repository;
    private readonly HashSet<string> _hydrated = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _hydrationLock = new(1, 1);

    public PersistentSourceCircuitBreaker(SourceCircuitBreaker breaker, SourceHealthRepository repository)
    {
        _breaker = breaker;
        _repository = repository;
    }

    /// <summary>
    /// Ensures <paramref name="sourceName"/>'s in-memory state has been hydrated from its
    /// persisted row (a no-op after the first call per source name, and safe to call repeatedly),
    /// then returns whether a call is currently permitted.
    /// </summary>
    public async Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        await EnsureHydratedAsync(sourceName, cancellationToken);
        return _breaker.CanCall(sourceName);
    }

    /// <summary>Records a success and persists the resulting state.</summary>
    public async Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        await EnsureHydratedAsync(sourceName, cancellationToken);
        _breaker.RecordSuccess(sourceName);
        await _repository.SaveAsync(sourceName, _breaker.GetSnapshot(sourceName), cancellationToken);
    }

    /// <summary>Records a failure and persists the resulting state.</summary>
    public async Task RecordFailureAsync(string sourceName, Exception ex, CancellationToken cancellationToken = default)
    {
        await EnsureHydratedAsync(sourceName, cancellationToken);
        _breaker.RecordFailure(sourceName, ex);
        await _repository.SaveAsync(sourceName, _breaker.GetSnapshot(sourceName), cancellationToken);
    }

    private async Task EnsureHydratedAsync(string sourceName, CancellationToken cancellationToken)
    {
        if (_hydrated.Contains(sourceName))
        {
            return;
        }

        await _hydrationLock.WaitAsync(cancellationToken);
        try
        {
            if (_hydrated.Add(sourceName))
            {
                var snapshot = await _repository.LoadAsync(sourceName, cancellationToken);
                _breaker.Seed(sourceName, snapshot);
            }
        }
        finally
        {
            _hydrationLock.Release();
        }
    }
}
