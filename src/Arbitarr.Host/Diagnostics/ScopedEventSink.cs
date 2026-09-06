using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitarr.Host.Diagnostics;

/// <summary>
/// Maps <see cref="RecordedEventKind"/> (Arbitarr.Core's mirror) onto the persisted
/// <see cref="EventKind"/> (Arbitarr.Data's). The mirror exists because Core may not reference
/// Data — see <see cref="RecordedEventKind"/>'s own note — and this is the single place the two
/// sides meet.
///
/// <c>EventKindMappingTests</c> asserts the mapping is total in both directions, so a kind added on
/// either side without the other fails a test instead of silently discarding events at runtime.
/// </summary>
public static class EventKindMapping
{
    /// <summary>The persisted kind corresponding to <paramref name="kind"/>.</summary>
    public static EventKind ToEntityKind(RecordedEventKind kind) => kind switch
    {
        RecordedEventKind.Decision => EventKind.Decision,
        RecordedEventKind.WorkerCycle => EventKind.WorkerCycle,
        RecordedEventKind.SnapshotRefreshed => EventKind.SnapshotRefreshed,
        RecordedEventKind.SearchServed => EventKind.SearchServed,
        RecordedEventKind.SourceFailed => EventKind.SourceFailed,

        // Deliberately throwing rather than defaulting to some "other" kind: a silently
        // mis-filed event is a worse outcome than a loud one, and ScopedEventSink swallows
        // this before it can reach the caller's critical path anyway.
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unmapped event kind."),
    };
}

/// <summary>
/// The real <see cref="IEventSink"/> (#55 step 2): resolves a scoped <see cref="EventRepository"/>
/// per event and writes one row.
///
/// WHY A SCOPE PER EVENT. The emitters are a singleton hosted worker and the request pipeline;
/// <see cref="EventRepository"/> is scoped because it wraps the scoped <c>ArbitarrDbContext</c>.
/// Capturing one context in a singleton sink would hold a single <c>DbContext</c> open for the
/// process lifetime and share it across concurrent writers — <c>DbContext</c> is not thread-safe,
/// so that is a data race, not merely untidy. <see cref="Caching.RefreshWorker"/> already resolves
/// its own per-cycle scope for exactly this reason.
///
/// WHY THIS SWALLOWS EVERY FAILURE. Recording that a search happened must never break serving the
/// search. Every write is wrapped: a validation rejection (a caller passing something
/// credential-shaped) and an infrastructure failure (the SQLite file unwritable) both cost the
/// event, not the request. That is the deliberate trade — this store is a history surface, not an
/// audit log with integrity requirements. The suppression audit log, which does have those
/// requirements, is written transactionally on the request path and is unaffected by this class.
/// </summary>
public sealed class ScopedEventSink : IEventSink
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScopedEventSink> _logger;

    public ScopedEventSink(IServiceScopeFactory scopeFactory, ILogger<ScopedEventSink> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(
        RecordedEventKind kind,
        string summary,
        string? reason = null,
        string? sourceDisplayName = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<EventRepository>();

            await repository.AddAsync(
                EventKindMapping.ToEntityKind(kind),
                summary,
                reason,
                sourceDisplayName,
                detail,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, or the client went away. Not a fault worth logging as one.
            throw;
        }
        catch (Exception ex)
        {
            // Logged at Warning, not Error: losing one history row degrades a diagnostic surface
            // and nothing else. Logging the kind and summary (never `detail`, which is free-form
            // and kind-specific) leaves enough to diagnose a systematic failure.
            _logger.LogWarning(
                ex,
                "Failed to record a {EventKind} activity event ({Summary}); the originating operation was unaffected.",
                kind,
                summary);
        }
    }
}
