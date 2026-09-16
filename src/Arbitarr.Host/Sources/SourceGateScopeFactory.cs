using Arbitarr.Data;
using Arbitarr.Data.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitarr.Host.Sources;

/// <summary>
/// The budget counter and backoff store resolved inside ONE scope, handed to a gate operation for
/// the length of that operation only (arb-x7w8.10). Both share that scope's
/// <c>ArbitarrDbContext</c>, which is safe because the operation that receives them runs to
/// completion before the scope is disposed and never hands them to another task.
/// </summary>
/// <param name="Counter">Counts API hits inside the source's rolling limits window.</param>
/// <param name="Backoff">Reads and writes the durable per-source backoff state.</param>
public readonly record struct SourceGate(SourceApiHitCounter Counter, SourceBackoffStore Backoff);

/// <summary>
/// Runs one gate operation against a freshly scoped <see cref="ArbitarrDbContext"/>
/// (arb-x7w8.10).
///
/// <para><b>THIS EXISTS BECAUSE THE SEARCH FAN-OUT IS CONCURRENT.</b> <c>UpstreamMergeStage</c>
/// launches every source's <c>SearchAsync</c> together under one <c>Task.WhenAll</c>, so all N
/// <see cref="BudgetedUpstreamSource"/> instances run at the same instant. <c>ArbitarrDbContext</c>
/// is scoped and NOT thread-safe: sharing one across that fan-out throws EF's "a second operation
/// was started on this context before a previous operation completed" as soon as a second source is
/// configured — turning the multi-indexer deployment this epic exists to enable into a crash on the
/// search path. An interface rather than a concrete type so a test can substitute a single-context
/// implementation without a service provider.</para>
/// </summary>
public interface ISourceGateScopeFactory
{
    /// <summary>
    /// Creates a scope, resolves the gate's collaborators from it, runs <paramref name="operation"/>
    /// to completion, then disposes the scope. The collaborators MUST NOT escape the callback: their
    /// context dies with the scope.
    /// </summary>
    Task<T> UseAsync<T>(
        Func<SourceGate, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="ISourceGateScopeFactory"/>: one DI scope per operation, the same shape
/// <c>DbClientApiKeyResolver</c> and <c>ScopedEventSink</c> use, and for the same reason.
///
/// <para>Registered as a SINGLETON and reaching the container through
/// <see cref="IServiceScopeFactory"/>, so it never captures a scoped context of its own. ADR 0017's
/// connection lifetime posture is preserved rather than worked around: each scope's context opens
/// its connection when the operation needs it and closes it when the scope is disposed, which is a
/// shorter lifetime than the request, not a longer one.</para>
/// </summary>
public sealed class SourceGateScopeFactory : ISourceGateScopeFactory
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SourceGateScopeFactory(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <inheritdoc />
    public async Task<T> UseAsync<T>(
        Func<SourceGate, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var scope = _scopeFactory.CreateAsyncScope();

        var gate = new SourceGate(
            scope.ServiceProvider.GetRequiredService<SourceApiHitCounter>(),
            scope.ServiceProvider.GetRequiredService<SourceBackoffStore>());

        return await operation(gate, cancellationToken).ConfigureAwait(false);
    }
}
