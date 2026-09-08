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

    /// <summary>
    /// A source failed, with the failure kind. When <c>SourceDisplayName</c> is populated, this
    /// event feeds <c>NotificationPolicy.FoldSourceFailure</c>'s per-source consecutive-failure
    /// counter (default threshold 3 — <c>NotificationSettings.DefaultConsecutiveFailureThreshold</c>,
    /// operator-configurable) and, on reaching it, produces a "source is down" notification. A null
    /// <c>SourceDisplayName</c> keeps the event out of that fold entirely — see the parameter doc
    /// below. <c>Arbitarr.Api.Search.DownloadProxyEndpoint</c> is a caller that relies on this to
    /// record a failure without arming the fold.
    /// </summary>
    SourceFailed,
}

/// <summary>
/// One event, as a value, so a caller with several to record can hand them over together — see
/// <see cref="IEventSink.RecordBatchAsync"/>. Field-for-field identical to
/// <see cref="IEventSink.RecordAsync"/>'s parameters, deliberately: this is the same event in a
/// shape that can go in a list, not a second model of one.
/// </summary>
/// <param name="Kind">Which kind of event this is.</param>
/// <param name="Summary">One line stating what happened. Required.</param>
/// <param name="Reason">Why it happened (plan AC2 — a reason, not only an event name), or null.</param>
/// <param name="SourceDisplayName">
/// The source involved, by display name or id — NEVER a credential (plan §9). Populating this on
/// a <see cref="RecordedEventKind.SourceFailed"/> event arms
/// <c>NotificationPolicy.FoldSourceFailure</c>'s per-source consecutive-failure counter; passing
/// null keeps the event out of that fold. See <see cref="RecordedEventKind.SourceFailed"/>.
/// </param>
/// <param name="Detail">Free-form kind-specific detail, or null.</param>
/// <param name="ShadowMode">
/// For a <see cref="RecordedEventKind.Decision"/>, whether the pipeline was in shadow mode when the
/// decision was made (#54 AC1); null for every other kind. Carried here as well as on
/// <see cref="IEventSink.RecordAsync"/> to keep the field-for-field correspondence above: a batched
/// decision that could not carry the flag would silently lose it, and FilterStage — the one caller
/// that batches — is exactly the caller that emits Decisions.
/// </param>
public readonly record struct RecordedEvent(
    RecordedEventKind Kind,
    string Summary,
    string? Reason = null,
    string? SourceDisplayName = null,
    string? Detail = null,
    bool? ShadowMode = null);

/// <summary>
/// The emission seam for the activity/history store (#55 step 2, plan §4 item 2).
///
/// Why an interface in Core rather than calling the repository directly: the two busiest emission
/// points are <see cref="Caching.RefreshWorker"/> (Arbitarr.Core) and the search-serving path
/// (Arbitarr.Api), and Core cannot reference Arbitarr.Data at all — see
/// <see cref="RecordedEventKind"/>. This is the same shape <see cref="IRefreshWorkerHealth"/>
/// already uses for the same reason, so it is the established local pattern rather than a new one.
///
/// IMPLEMENTATIONS MUST NOT THROW. Recording history is strictly less important than serving the
/// request that produced it: a failed write here must never fail a search or abort a worker cycle.
/// <see cref="NullEventSink"/> is the no-op default, so a caller that was never wired to a real
/// sink degrades to recording nothing rather than NullReferenceException-ing on a hot path.
///
/// THEY DO, HOWEVER, COST THE CALLER THE WRITE'S LATENCY. The real sink awaits its database write
/// rather than posting it to a background queue, so this is not free and a caller must not treat it
/// as such. That is a deliberate choice, not an oversight: fire-and-forget would need its own
/// bounded queue, drain-on-shutdown and overflow policy, and #55 is not the place to introduce a
/// second lifetime-managed background writer. The obligation it puts on callers instead is to
/// record ONE event per thing that happened rather than looping — which is what
/// <see cref="RecordBatchAsync"/> exists for.
/// </summary>
public interface IEventSink
{
    /// <summary>
    /// Records one event, awaiting the write. A failed write is swallowed by the implementation, so
    /// completion means "the sink is done with this", not "the row is durable" — see the interface
    /// note. Use <see cref="RecordBatchAsync"/> when there is more than one event to record.
    /// </summary>
    /// <param name="kind">Which kind of event this is.</param>
    /// <param name="summary">One line stating what happened. Required.</param>
    /// <param name="reason">Why it happened (plan AC2 — a reason, not only an event name), or null.</param>
    /// <param name="sourceDisplayName">
    /// The source involved, by display name or id — NEVER a credential (plan §9). Populating this
    /// on a <see cref="RecordedEventKind.SourceFailed"/> event arms
    /// <c>NotificationPolicy.FoldSourceFailure</c>'s per-source consecutive-failure counter;
    /// passing null keeps the event out of that fold. See
    /// <see cref="RecordedEventKind.SourceFailed"/>.
    /// </param>
    /// <param name="detail">Free-form kind-specific detail, or null.</param>
    /// <param name="shadowMode">
    /// For a <see cref="RecordedEventKind.Decision"/>, whether the pipeline was in shadow mode when
    /// the decision was made (#54 AC1). Null for every other kind, where the question does not
    /// apply. Recorded as a stored flag rather than left to be inferred from the summary's wording,
    /// so that a decision made under shadow mode still reads as one after the switch is flipped.
    /// </param>
    ValueTask RecordAsync(
        RecordedEventKind kind,
        string summary,
        string? reason = null,
        string? sourceDisplayName = null,
        string? detail = null,
        CancellationToken cancellationToken = default,
        bool? shadowMode = null);

    /// <summary>
    /// Records a burst of events that happened at one point, as one unit of work.
    ///
    /// This exists because <see cref="RecordAsync"/> awaited in a loop violates the interface's own
    /// "must not block the caller's work" contract at scale: <c>FilterStage</c> emits one Decision
    /// per suppressed release and can suppress dozens within a single search, so a loop becomes
    /// dozens of sequential scope-creations and database round trips ON the search path. A caller
    /// with more than one event to record should use this instead.
    ///
    /// The default implementation is the loop, so an existing sink (and any test double) keeps
    /// working unchanged and merely fails to get the batching benefit. The real sink overrides it.
    /// </summary>
    /// <param name="events">The events to record. An empty list is a no-op.</param>
    async ValueTask RecordBatchAsync(
        IReadOnlyList<RecordedEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        foreach (var e in events)
        {
            await RecordAsync(e.Kind, e.Summary, e.Reason, e.SourceDisplayName, e.Detail, cancellationToken, e.ShadowMode)
                .ConfigureAwait(false);
        }
    }
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
        CancellationToken cancellationToken = default,
        bool? shadowMode = null) => ValueTask.CompletedTask;
}
