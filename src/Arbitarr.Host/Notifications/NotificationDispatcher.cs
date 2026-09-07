using Arbitarr.Core.Notifications;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Arbitarr.Data.Notifications;

namespace Arbitarr.Host.Notifications;

/// <summary>
/// #57's evaluation cycle: reads new rows from the shared event store, folds them through
/// <see cref="NotificationPolicy"/>, delivers whatever the policy decided, and persists the state.
/// This is the one place the store, the policy and the transport meet, and it lives in
/// Arbitarr.Host because that is the sole DI composition root (AC6) and the only project permitted
/// to reference both Core and Data.
///
/// <para><b>IT POLLS. THERE IS NO SUBSCRIPTION, AND THAT IS THE DESIGN.</b> The event store is a
/// SQLite table read through <c>EventRepository.QueryAsync</c>; #55 established a seek-cursor read
/// path over it and this reuses it unchanged. A pub/sub layer over the same table would be a second
/// concurrency model for no gain at homelab scale — and polling is the better fit anyway, because
/// §3.1's transition detection must survive a restart, which requires a durable position regardless.
/// The cursor gives it that for free. Do not "upgrade" this to an observer.</para>
///
/// <para><b>The cursor orders by Id, never by OccurredAt.</b> <c>EventQuery.Cursor</c> explains it
/// in full: OccurredAt is non-unique — a burst of suppressions from one search shares a single
/// instant — and a cursor on a non-unique key silently drops or repeats the ties. Id is unique and
/// monotonic, so it totally orders the table.</para>
///
/// <para><b>No webhook URL is in scope here.</b> This type never reads one. It hands the payload to
/// <see cref="WebhookNotificationTransport"/>, which reads the URL from the repository itself at
/// the moment of the POST. So the URL cannot reach a payload, a log line, or an event row through
/// this path even by accident — the class that decides WHAT to send never learns WHERE.</para>
/// </summary>
public sealed class NotificationDispatcher
{
    /// <summary>
    /// How many event rows one cycle folds in. A ceiling rather than "everything since the cursor"
    /// so that a notifier which has been off for a week does not materialize the entire operational
    /// retention window in host memory on its first cycle. The cursor advances regardless, so a
    /// backlog drains over consecutive cycles instead of arriving as one page.
    /// </summary>
    public const int BatchSize = 200;

    private readonly EventRepository _events;
    private readonly NotificationRepository _notifications;
    private readonly WebhookNotificationTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public NotificationDispatcher(
        EventRepository events,
        NotificationRepository notifications,
        WebhookNotificationTransport transport,
        TimeProvider timeProvider,
        ILogger<NotificationDispatcher>? logger = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationDispatcher>.Instance;
    }

    /// <summary>
    /// Runs one evaluation cycle. Returns what was delivered, so tests and the hosted service can
    /// observe a cycle without inspecting the database.
    ///
    /// Public so tests drive cycles directly rather than waiting on a timer — the same shape
    /// <c>ClassifierPollingWorker.RunCycleAsync</c> uses for the same reason.
    /// </summary>
    public async Task<IReadOnlyList<NotificationPayload>> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _notifications.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var state = await _notifications.GetStateAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.Enabled)
        {
            // Disabled means evaluate nothing — but ALSO means do not silently accumulate a backlog
            // that would all fire the moment it is switched on. The cursor is advanced to the head
            // of the store without notifying, so enabling notifications starts from now rather than
            // replaying however many days of history the operator was not asking about.
            await AdvanceCursorWithoutNotifyingAsync(state, cancellationToken).ConfigureAwait(false);
            return Array.Empty<NotificationPayload>();
        }

        var batch = await ReadAscendingBatchAsync(state.Cursor, cancellationToken).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return Array.Empty<NotificationPayload>();
        }

        var observations = new List<NotificationObservation>(batch.Count);
        foreach (var row in batch)
        {
            if (Observe(row) is { } observation)
            {
                observations.Add(observation);
            }
        }

        var policy = new NotificationPolicy(settings);
        var decision = policy.Evaluate(
            state,
            observations,
            cursor: batch[^1].Id,
            _timeProvider.GetUtcNow());

        // State is persisted BEFORE delivery, and that ordering is deliberate. Delivery is
        // best-effort and may fail or be slow; if the process died between a successful delivery
        // and a state write, the next cycle would re-fold the same rows and notify again. Writing
        // first means the worst case is a notification the operator never receives (which the last
        // delivery result surfaces) rather than a duplicate storm, and §3.4 is explicit that a
        // notification is best-effort while §3.1 is explicit that duplicates are the failure mode
        // that makes people mute the feature.
        await _notifications.SetStateAsync(decision.State, cancellationToken).ConfigureAwait(false);

        foreach (var notification in decision.Notifications)
        {
            await DeliverAsync(notification, cancellationToken).ConfigureAwait(false);
        }

        return decision.Notifications;
    }

    /// <summary>
    /// Delivers one notification and records its outcome. Never throws for a delivery failure —
    /// §3.4/AC6: a broken webhook must never fail or delay the operation that produced the event,
    /// and here the "operation" is the cycle itself, which must continue to the next notification.
    /// </summary>
    private async Task DeliverAsync(NotificationPayload payload, CancellationToken cancellationToken)
    {
        var url = await _notifications.ReadWebhookUrlForDeliveryAsync(cancellationToken).ConfigureAwait(false);
        var outcome = await _transport.DeliverAsync(url, payload, cancellationToken).ConfigureAwait(false);

        await _notifications.RecordDeliveryAsync(outcome, cancellationToken).ConfigureAwait(false);

        if (outcome != NotificationDeliveryOutcome.Delivered)
        {
            // The TRIGGER and the OUTCOME, never the target. Both are closed enums, so this log
            // line cannot carry the webhook URL — which matters because logs are surfaced in the
            // UI (#65) and the URL is the credential.
            _logger.LogWarning(
                "Notification for {Trigger} was not delivered ({Outcome}); the pipeline was unaffected.",
                payload.Trigger,
                outcome);
        }
    }

    /// <summary>
    /// Moves the cursor to the head of the store without evaluating anything, for the disabled
    /// case. Reads the newest row's id only.
    /// </summary>
    private async Task AdvanceCursorWithoutNotifyingAsync(NotificationState state, CancellationToken cancellationToken)
    {
        var head = await _events.QueryAsync(new EventQuery(Limit: 1), cancellationToken).ConfigureAwait(false);
        var newest = head.Events.Count > 0 ? head.Events[0].Id : (long?)null;

        if (newest is null || newest == state.Cursor)
        {
            return;
        }

        await _notifications
            .SetStateAsync(state with { Cursor = newest }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads up to <see cref="BatchSize"/> rows newer than <paramref name="cursor"/>, returned
    /// OLDEST FIRST.
    ///
    /// <c>QueryAsync</c> pages backwards from the newest row (its cursor means "strictly older than
    /// this"), which is what an activity feed wants and the exact opposite of what a fold wants.
    /// The re-read and reversal here is that adaptation, and it is why the batch is capped: this
    /// reads the newest page and keeps only the rows above the notifier's position, which is
    /// correct and cheap while the notifier is keeping up.
    /// </summary>
    private async Task<List<EventEntry>> ReadAscendingBatchAsync(long? cursor, CancellationToken cancellationToken)
    {
        var page = await _events.QueryAsync(new EventQuery(Limit: BatchSize), cancellationToken).ConfigureAwait(false);

        var fresh = new List<EventEntry>(page.Events.Count);
        foreach (var row in page.Events)
        {
            if (cursor is { } position && row.Id <= position)
            {
                // Descending order, so everything below this row is older still.
                break;
            }

            fresh.Add(row);
        }

        fresh.Reverse();
        return fresh;
    }

    /// <summary>
    /// Reduces one stored row to what the policy interprets, or null for the kinds it does not act
    /// on. This is the ONLY place the store's taxonomy is translated into the notifier's, which is
    /// what keeps <see cref="NotificationPolicy"/> free of any opinion about event kinds.
    /// </summary>
    private static NotificationObservation? Observe(EventEntry row) => row.Kind switch
    {
        EventKind.SourceFailed => new NotificationObservation(
            ObservedEventKind.SourceFailure, row.SourceDisplayName, row.OccurredAt),

        // A snapshot refresh or a completed worker cycle is the recovery signal: the store has no
        // "source recovered" row and needs none, because a source producing a successful cycle is,
        // by observation, answering again. See ObservedEventKind.SourceSuccess.
        EventKind.SnapshotRefreshed or EventKind.WorkerCycle => new NotificationObservation(
            ObservedEventKind.SourceSuccess, row.SourceDisplayName, row.OccurredAt),

        EventKind.Decision => new NotificationObservation(
            IsShadowed(row.Detail) ? ObservedEventKind.ShadowedSuppression : ObservedEventKind.EnforcedSuppression,
            row.SourceDisplayName,
            row.OccurredAt),

        // SearchServed is volume, not signal; nothing in §3.1 acts on it.
        _ => null,
    };

    /// <summary>
    /// Whether a Decision row was shadow-flagged rather than enforced, read from the structured
    /// <c>shadow=</c> token <c>FilterStage</c> writes into Detail.
    ///
    /// A row without the token reads as ENFORCED, which is the safe default in both directions: it
    /// keeps rows written before the token existed counted in the numerator (so the rate is not
    /// silently understated after an upgrade), and an understated rate is the failure that leaves
    /// an operator uninformed, whereas an overstated one merely notifies them to look.
    /// </summary>
    private static bool IsShadowed(string? detail) =>
        detail is not null && detail.Contains("shadow=True", StringComparison.OrdinalIgnoreCase);
}
