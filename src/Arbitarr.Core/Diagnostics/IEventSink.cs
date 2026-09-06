namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// What an <see cref="IEventSink"/> records, mirroring the persisted store's kinds one-for-one
/// (#55 step 2). This enum exists ONLY because <c>Arbitarr.Core</c> may not reference
/// <c>Arbitarr.Data</c> — <c>Arbitarr.Architecture.Tests.CoreIsolationTests</c> asserts Core has
/// zero references to any other Arbitarr project, and Core is where the emitting worker lives.
///
/// It is therefore a deliberate mirror, not a second taxonomy: <c>EventKindMapping</c> in
/// <c>Arbitarr.Host</c> maps these onto <c>Arbitarr.Data.Entities.EventKind</c>, and a test there
/// asserts the two enums stay exhaustive against each other, so adding a kind on one side without
/// the other fails the build's tests rather than silently dropping events.
/// </summary>
public enum RecordedEventKind
{
    /// <summary>A pipeline decision (suppress / de-rank). #54's territory.</summary>
    Decision,

    /// <summary>The worker cycle ran, with what it did.</summary>
    WorkerCycle,

    /// <summary>A query snapshot was refreshed, and why.</summary>
    SnapshotRefreshed,

    /// <summary>A search was served, distinguishing cache-served from live-query-served.</summary>
    SearchServed,

    /// <summary>A source failed, with the failure kind.</summary>
    SourceFailed,
}

/// <summary>
/// The emission seam for the activity/history store (#55 step 2, plan §4 item 2).
///
/// Why an interface in Core rather than calling the repository directly: the two busiest emission
/// points are <see cref="Caching.RefreshWorker"/> (Arbitarr.Core) and the search-serving path
/// (Arbitarr.Api), and Core cannot reference Arbitarr.Data at all — see
/// <see cref="RecordedEventKind"/>. This is the same shape <see cref="IRefreshWorkerHealth"/>
/// already uses for the same reason, so it is the established local pattern rather than a new one.
///
/// IMPLEMENTATIONS MUST NOT THROW AND MUST NOT BLOCK THE CALLER'S WORK. Recording history is
/// strictly less important than serving the request that produced it: a failed write here must
/// never fail a search or abort a worker cycle. <see cref="NullEventSink"/> is the no-op default,
/// so a caller that was never wired to a real sink degrades to recording nothing rather than
/// NullReferenceException-ing on a hot path.
/// </summary>
public interface IEventSink
{
    /// <summary>
    /// Records one event. Returns as soon as the event is accepted for writing — see the interface
    /// note: callers await this only to propagate cancellation, never to confirm durability.
    /// </summary>
    /// <param name="kind">Which kind of event this is.</param>
    /// <param name="summary">One line stating what happened. Required.</param>
    /// <param name="reason">Why it happened (plan AC2 — a reason, not only an event name), or null.</param>
    /// <param name="sourceDisplayName">The source involved, by display name or id — NEVER a credential (plan §9).</param>
    /// <param name="detail">Free-form kind-specific detail, or null.</param>
    ValueTask RecordAsync(
        RecordedEventKind kind,
        string summary,
        string? reason = null,
        string? sourceDisplayName = null,
        string? detail = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The no-op sink: records nothing, successfully. The default wherever a sink is optional, so the
/// many existing constructor call sites (tests especially) keep working unchanged and an unwired
/// caller silently records nothing instead of faulting the path it sits on.
/// </summary>
public sealed class NullEventSink : IEventSink
{
    /// <summary>The shared instance; this type holds no state.</summary>
    public static readonly NullEventSink Instance = new();

    private NullEventSink()
    {
    }

    /// <inheritdoc />
    public ValueTask RecordAsync(
        RecordedEventKind kind,
        string summary,
        string? reason = null,
        string? sourceDisplayName = null,
        string? detail = null,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
