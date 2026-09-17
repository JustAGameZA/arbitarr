using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.CircuitBreaker;

/// <summary>
/// Thin persistence adapter translating <see cref="SourceCircuitBreaker"/>'s pure, dependency-free
/// <see cref="CircuitBreakerSnapshot"/> to/from the <see cref="SourceHealthRecord"/> table, so the
/// state machine itself never touches SQLite/EF and stays independently unit-testable. Use this to
/// (a) hydrate a <see cref="SourceCircuitBreaker"/> with its last-persisted state on startup and
/// (b) persist state after each observed transition, so breaker state survives restarts.
/// </summary>
public sealed class SourceHealthRepository
{
    private readonly ArbitarrDbContext _dbContext;

    public SourceHealthRepository(ArbitarrDbContext dbContext)
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
        BaseBackoff: TimeSpan.FromSeconds(record.BaseBackoffSeconds),
        CurrentBackoff: TimeSpan.FromSeconds(record.CurrentBackoffSeconds),
        LastFailureAt: record.LastFailureAt,
        LastSuccessAt: record.LastSuccessAt,
        LastError: record.LastError,
        LastOutcome: ToOutcome(record.LastOutcome),
        LastUpstreamStatusCode: record.LastUpstreamStatusCode,
        NextProbeAt: record.NextProbeAt);

    private static void ApplySnapshot(SourceHealthRecord record, CircuitBreakerSnapshot snapshot)
    {
        record.State = ToEntityState(snapshot.State);
        record.ConsecutiveFailures = snapshot.ConsecutiveFailures;
        record.BaseBackoffSeconds = snapshot.BaseBackoff.TotalSeconds;
        record.CurrentBackoffSeconds = snapshot.CurrentBackoff.TotalSeconds;
        record.LastFailureAt = snapshot.LastFailureAt;
        record.LastSuccessAt = snapshot.LastSuccessAt;
        record.LastError = snapshot.LastError;
        record.LastOutcome = snapshot.LastOutcome.ToString();
        record.LastUpstreamStatusCode = snapshot.LastUpstreamStatusCode;
        record.NextProbeAt = snapshot.NextProbeAt;
    }

    /// <summary>
    /// arb-mhd2: reads a persisted <see cref="SourceHealthRecord.LastOutcome"/> NAME back into the
    /// enum by matching the names EXPLICITLY.
    ///
    /// <para><b><c>Enum.TryParse</c> is deliberately not used here, and this is not a style
    /// preference</b> (CLAUDE.md section 3). It also accepts the NUMERIC form, so a stored "4" would
    /// select <see cref="SourceStatusOutcome.AuthRejected"/> through an input shape no writer in this
    /// repository produces — <see cref="ApplySnapshot"/> always writes
    /// <see cref="System.Enum.ToString()"/>. <c>Enum.IsDefined</c> would not close it either, since
    /// <c>4</c> IS defined. Matching the names by hand makes the stored format closed by
    /// construction: a value that is not one of these exact strings cannot mint a member.</para>
    ///
    /// <para>Anything unrecognised — a null from a row written before the column existed, an empty
    /// string, a name from a future version, a hand-edited value — becomes
    /// <see cref="SourceStatusOutcome.Unknown"/>. Never a guess inferred from
    /// <see cref="SourceHealthRecord.LastError"/>'s text, which would resurrect the projection-time
    /// classification of sanitised prose that arb-mhd2 exists to remove.</para>
    /// </summary>
    private static SourceStatusOutcome ToOutcome(string? stored) => stored switch
    {
        nameof(SourceStatusOutcome.None) => SourceStatusOutcome.None,
        nameof(SourceStatusOutcome.UpstreamError) => SourceStatusOutcome.UpstreamError,
        nameof(SourceStatusOutcome.Unreachable) => SourceStatusOutcome.Unreachable,
        nameof(SourceStatusOutcome.Timeout) => SourceStatusOutcome.Timeout,
        nameof(SourceStatusOutcome.AuthRejected) => SourceStatusOutcome.AuthRejected,
        nameof(SourceStatusOutcome.InternalError) => SourceStatusOutcome.InternalError,
        _ => SourceStatusOutcome.Unknown,
    };

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
