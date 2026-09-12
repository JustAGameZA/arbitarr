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
        RecordedEventKind.SourceQueryHit => EventKind.SourceQueryHit,
        RecordedEventKind.SourceGrabHit => EventKind.SourceGrabHit,

        // Unreachable in practice, and that is the point: EventKindMappingTests asserts this
        // mapping is total over both enums, so a kind added on one side without the other fails
        // the test suite at build time — which is the actual guard. This arm exists so the
        // compiler's exhaustiveness check has an answer and so a value cast in from outside the
        // enum's range fails loudly rather than being filed under some "other" kind; a silently
        // mis-filed event is worse than a thrown one, and ScopedEventSink swallows this before it
        // can reach the caller's critical path anyway.
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
/// so that is a data race, not merely untidy. <see cref="Arbitarr.Core.Caching.RefreshWorker"/> already resolves
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
        CancellationToken cancellationToken = default,
        bool? shadowMode = null)
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
                cancellationToken,
                shadowMode);
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

    /// <inheritdoc />
    public async ValueTask RecordBatchAsync(
        IReadOnlyList<RecordedEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        try
        {
            // ONE scope and ONE SaveChangesAsync for the whole burst, which is the entire reason
            // this override exists: the default implementation loops RecordAsync, and on the
            // suppression path that is one scope plus one database round trip PER SUPPRESSED
            // RELEASE, awaited in sequence while a search waits. Same swallow-everything posture
            // as RecordAsync — a burst that fails costs the events, never the search.
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<EventRepository>();

            var rows = events
                .Select(e => (
                    Kind: EventKindMapping.ToEntityKind(e.Kind),
                    e.Summary,
                    e.Reason,
                    e.SourceDisplayName,
                    e.Detail,
                    e.ShadowMode))
                .ToList();

            await repository.AddRangeAsync(rows, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The batch is one transaction, so a failure loses all of it — hence the count, which
            // is what distinguishes "one bad row" from "the database is unwritable" in the log.
            _logger.LogWarning(
                ex,
                "Failed to record a batch of {EventCount} activity events; the originating operation was unaffected.",
                events.Count);
        }
    }
}
