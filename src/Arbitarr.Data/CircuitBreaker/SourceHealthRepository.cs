using ArrSearcher.Core.Sources.CircuitBreaker;
using ArrSearcher.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArrSearcher.Data.CircuitBreaker;

/// <summary>
/// Thin persistence adapter translating <see cref="SourceCircuitBreaker"/>'s pure, dependency-free
/// <see cref="CircuitBreakerSnapshot"/> to/from the <see cref="SourceHealthRecord"/> table, so the
/// state machine itself never touches SQLite/EF and stays independently unit-testable. Use this to
/// (a) hydrate a <see cref="SourceCircuitBreaker"/> with its last-persisted state on startup and
/// (b) persist state after each observed transition, so breaker state survives restarts.
/// </summary>
public sealed class SourceHealthRepository
{
    private readonly ArrSearcherDbContext _dbContext;

    public SourceHealthRepository(ArrSearcherDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>Loads the persisted snapshot for a source, or <see cref="CircuitBreakerSnapshot.Initial"/> if no row exists yet.</summary>
    public async Task<CircuitBreakerSnapshot> LoadAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.SourceHealthRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.SourceName == sourceName, cancellationToken);

        return record is null ? CircuitBreakerSnapshot.Initial : ToSnapshot(record);
    }

    /// <summary>Loads persisted snapshots for every source that currently has a row, keyed by source name.</summary>
    public async Task<IReadOnlyDictionary<string, CircuitBreakerSnapshot>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var records = await _dbContext.SourceHealthRecords.AsNoTracking().ToListAsync(cancellationToken);
        return records.ToDictionary(r => r.SourceName, ToSnapshot, StringComparer.Ordinal);
    }

    /// <summary>
    /// Upserts the given snapshot for a source, creating the <see cref="SourceHealthRecord"/> row
    /// if it does not yet exist.
    /// </summary>
    public async Task SaveAsync(string sourceName, CircuitBreakerSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.SourceHealthRecords
            .SingleOrDefaultAsync(r => r.SourceName == sourceName, cancellationToken);

        if (record is null)
        {
            record = new SourceHealthRecord { SourceName = sourceName };
            _dbContext.SourceHealthRecords.Add(record);
        }

        ApplySnapshot(record, snapshot);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static CircuitBreakerSnapshot ToSnapshot(SourceHealthRecord record) => new(
        State: ToCoreState(record.State),
        ConsecutiveFailures: record.ConsecutiveFailures,
        CurrentBackoff: TimeSpan.FromSeconds(record.CurrentBackoffSeconds),
        LastFailureAt: record.LastFailureAt,
        LastSuccessAt: record.LastSuccessAt,
        LastError: record.LastError,
        NextProbeAt: record.NextProbeAt);

    private static void ApplySnapshot(SourceHealthRecord record, CircuitBreakerSnapshot snapshot)
    {
        record.State = ToEntityState(snapshot.State);
        record.ConsecutiveFailures = snapshot.ConsecutiveFailures;
        record.CurrentBackoffSeconds = snapshot.CurrentBackoff.TotalSeconds;
        record.LastFailureAt = snapshot.LastFailureAt;
        record.LastSuccessAt = snapshot.LastSuccessAt;
        record.LastError = snapshot.LastError;
        record.NextProbeAt = snapshot.NextProbeAt;
    }

    private static CircuitState ToCoreState(CircuitBreakerState state) => state switch
    {
        CircuitBreakerState.Closed => CircuitState.Closed,
        CircuitBreakerState.Open => CircuitState.Open,
        CircuitBreakerState.HalfOpen => CircuitState.HalfOpen,
        _ => throw new InvalidOperationException($"Unknown persisted circuit breaker state: {state}"),
    };

    private static CircuitBreakerState ToEntityState(CircuitState state) => state switch
    {
        CircuitState.Closed => CircuitBreakerState.Closed,
        CircuitState.Open => CircuitBreakerState.Open,
        CircuitState.HalfOpen => CircuitBreakerState.HalfOpen,
        _ => throw new InvalidOperationException($"Unknown circuit breaker state: {state}"),
    };
}
