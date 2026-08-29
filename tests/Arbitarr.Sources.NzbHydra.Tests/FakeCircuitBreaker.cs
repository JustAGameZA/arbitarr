using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Sources.NzbHydra.Tests;

/// <summary>Trivial in-memory <see cref="IAsyncCircuitBreaker"/> test double.</summary>
internal sealed class FakeCircuitBreaker : IAsyncCircuitBreaker
{
    private bool _canCall = true;

    public int CanCallCallCount { get; private set; }
    public int SuccessCount { get; private set; }
    public List<Exception> Failures { get; } = new();

    public void SetCanCall(bool value) => _canCall = value;

    public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        CanCallCallCount++;
        return Task.FromResult(_canCall);
    }

    public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        SuccessCount++;
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default)
    {
        Failures.Add(exception);
        return Task.CompletedTask;
    }
}
