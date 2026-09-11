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

        var batch = await ReadAscendingBatchAsync(state.Cursor, state.RepeatsSeenAt, cancellationToken).ConfigureAwait(false);
        if (batch.Count == 0)
        {
            return Array.Empty<NotificationPayload>();
        }

        var observations = new List<NotificationObservation>(batch.Count);
        foreach (var row in batch)
        {
            // A row at or below the cursor is here because it REPEATED, not because it is new.
            // See Observe: a fresh row presents its whole RepeatCount (all of it is unseen), a
            // re-read row presents one occurrence (the watermark cannot say how many).
            var isRepeat = state.Cursor is { } position && row.Id <= position;
            if (Observe(row, isRepeat) is { } observation)
            {
                observations.Add(observation);
            }
        }

        // The cursor is the HIGHEST Id folded, not the last element: since arb-u8e the batch is
        // ordered by last activity, so its final row is the most recent repeat and may sit well
        // below the newest Id. Taking batch[^1].Id here would walk the cursor backwards and
        // re-notify everything above it on the next pass.
        var cursor = batch.Max(row => row.Id);
        if (state.Cursor is { } previous && previous > cursor)
        {
            cursor = previous;
        }

        // The watermark advances to the newest repeat FOLDED IN THIS BATCH, and only here — after
        // the fold, never before it. A crash between the read and this point therefore re-presents
        // the same repeats on the next cycle rather than skipping them: an over-count trips the
        // threshold early, a skip makes it unreachable, and only one of those is recoverable.
        var repeatsSeenAt = state.RepeatsSeenAt;
        foreach (var row in batch)
        {
            if (row.LastRepeatedAt is { } repeatedAt && (repeatsSeenAt is not { } seen || repeatedAt > seen))
            {
                repeatsSeenAt = repeatedAt;
            }
        }

        var policy = new NotificationPolicy(settings);
        var decision = policy.Evaluate(
            state,
            observations,
            cursor: cursor,
            _timeProvider.GetUtcNow(),
            repeatsSeenAt);

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
    ///
    /// <para>The repeat watermark is moved to NOW at the same time, and it has to be: since arb-u8e
    /// the position is a pair, and advancing only the Id half would leave every row below the new
    /// cursor that has ever repeated looking like an unseen repeat. Enabling notifications would
    /// then re-read the whole coalesced history at once — the backlog replay this method exists to
    /// prevent, arriving through the other half of the cursor.</para>
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
            .SetStateAsync(
                state with { Cursor = newest, RepeatsSeenAt = _timeProvider.GetUtcNow() },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads up to <see cref="BatchSize"/> rows the notifier has not yet acted on, returned
    /// OLDEST FIRST: rows newer than <paramref name="cursor"/>, plus rows the cursor has already
    /// passed that have REPEATED since <paramref name="repeatsSeenAt"/>.
    ///
    /// <c>QueryAsync</c> pages backwards from the newest row (its cursor means "strictly older than
    /// this"), which is what an activity feed wants and the exact opposite of what a fold wants.
    /// The re-read and reversal here is that adaptation, and it is why the batch is capped: this
    /// reads the newest page and keeps only the rows above the notifier's position, which is
    /// correct and cheap while the notifier is keeping up.
    ///
    /// <para><b>arb-u8e: the Id cursor alone silently stopped the notifier working.</b> A repeat
    /// folds onto the EXISTING row — same Id, <c>RepeatCount</c> incremented,
    /// <c>LastRepeatedAt</c> advanced — so a source that failed once, was folded in, and then kept
    /// failing onto that same row never came back past the cursor. The threshold could then never
    /// be reached, which is the "source is down" notification ceasing to work in exactly the
    /// repeated-failure case it exists for. Rows below the cursor whose <c>LastRepeatedAt</c> is
    /// later than the watermark are therefore re-read.</para>
    ///
    /// <para><b>Re-read rows are ordered by LAST ACTIVITY, not by Id, and that is the one exception
    /// to the ordering rule on this class.</b> It does not contradict it: Id remains the only thing
    /// that orders the STORE, because OccurredAt is non-unique, and Id still breaks ties here. But a
    /// repeat's position in the fold is when it REPEATED, not when its row was first written, and
    /// the fold is order-sensitive because a success removes a source from the failing set.
    ///
    /// <b>This ordering is DEFENSIVE, and today it is unreachable — deliberately kept anyway.</b>
    /// The case it guards is a repeat whose instant is later than a newer row's, which would make Id
    /// order and time order disagree about whether a source is up. That cannot currently happen:
    /// <c>EventRepository.TryCoalesceAsync</c> folds only onto the GLOBALLY newest row
    /// (<c>OrderByDescending(e =&gt; e.Id).FirstOrDefaultAsync()</c>, with no source filter), so any
    /// intervening event from any source ends the fold and a repeat can only ever land on the
    /// highest-Id row in the store. Ordering by Id alone would therefore behave identically, and no
    /// test can tell the two apart — that was measured, not assumed.
    ///
    /// It stays because this read model's correctness should not silently depend on a coalescing
    /// detail two layers away, and the cost is one comparison.
    /// <c>NotificationDispatcherTests.A_failure_after_a_recovery_starts_a_new_row_rather_than_folding_backwards</c>
    /// is the tripwire: it pins that upstream invariant, so widening coalescing to match any row in
    /// the window fails there and brings whoever does it back to this paragraph.</para>
    ///
    /// <para><b>A re-read row presents ONE occurrence, which deliberately under-counts.</b> The
    /// watermark is a single value, so it says "this row repeated since you last looked" and cannot
    /// say how many times. A storm that folds several repeats between two polls therefore advances
    /// the count by one rather than by the true number, so the threshold trips LATE — never never,
    /// which is today's behaviour. The exact alternative needs a per-row count, and
    /// <c>NotificationRepository</c> requires its rows be fixed in number precisely so nothing
    /// accumulates without a <c>MaintenanceJob</c> prune. Late-but-correct was chosen over exact-
    /// but-unbounded. <c>RepeatCount</c> is NOT used as the figure here: it counts the first
    /// occurrence too, so it is a total, not a growth.</para>
    /// </summary>
    private async Task<List<EventEntry>> ReadAscendingBatchAsync(
        long? cursor,
        DateTimeOffset? repeatsSeenAt,
        CancellationToken cancellationToken)
    {
        var page = await _events.QueryAsync(new EventQuery(Limit: BatchSize), cancellationToken).ConfigureAwait(false);

        var batch = new List<EventEntry>(page.Events.Count);
        foreach (var row in page.Events)
        {
            if (cursor is not { } position || row.Id > position)
            {
                batch.Add(row);
                continue;
            }

            // At or below the cursor: already folded in once. It comes back only if it has
            // repeated since the watermark. Unlike the fresh case this cannot break out of the
            // loop — a repeat lives on an OLD row, so the interesting rows are exactly the ones
            // below the cursor and stopping here is what hid the bug.
            if (row.LastRepeatedAt is { } repeatedAt
                && (repeatsSeenAt is not { } seen || repeatedAt > seen))
            {
                batch.Add(row);
            }
        }

        // Real time order, oldest first — see the doc comment on why this is by last activity
        // rather than by Id. Id breaks ties, because a burst shares one instant and List.Sort is
        // NOT stable: without the tiebreak, rows written at the same moment could fold in a
        // different order on different runs, and the fold is order-sensitive.
        return batch
            .OrderBy(row => row.LastRepeatedAt ?? row.OccurredAt)
            .ThenBy(row => row.Id)
            .ToList();
    }

    /// <summary>
    /// Reduces one stored row to what the policy interprets, or null for the kinds it does not act
    /// on. This is the ONLY place the store's taxonomy is translated into the notifier's, which is
    /// what keeps <see cref="NotificationPolicy"/> free of any opinion about event kinds.
    ///
    /// <para><paramref name="isRepeat"/> says the row is being re-read below the cursor, which
    /// changes how many occurrences it stands for — see the count comment below.</para>
    /// </summary>
    private static NotificationObservation? Observe(EventEntry row, bool isRepeat) => row.Kind switch
    {
        // RepeatCount is carried through on a row seen for the FIRST time: since arb-itw a single
        // stored row can stand for N identical failures, and the consecutive-failure threshold
        // counts FAILURES, not rows. Dropping it would mean a source failing in a tight retry loop
        // — the exact case coalescing folds onto one row — never reaching the threshold and never
        // being reported as down, which is the notification silently ceasing to work.
        //
        // A RE-READ row presents 1, not RepeatCount, because RepeatCount is a TOTAL that counts
        // the first occurrence (EventEntry documents the default as 1) and that total has already
        // been presented. Re-presenting it would count every earlier failure again on every pass,
        // so a single stale row would trip the threshold on its own. One is the deliberate
        // under-count the watermark forces: late, never never.
        //
        // A re-read row also reports WHEN IT REPEATED rather than when it was first written. The
        // policy windows on this instant (Evaluate drops observations older than the rate window),
        // so a row first seen days ago but repeating now would otherwise be read as ancient and
        // dropped — re-reading it and then discarding it would fix nothing. A fresh row keeps
        // OccurredAt, unchanged from before.
        EventKind.SourceFailed => new NotificationObservation(
            ObservedEventKind.SourceFailure,
            row.SourceDisplayName,
            isRepeat ? row.LastRepeatedAt ?? row.OccurredAt : row.OccurredAt,
            isRepeat ? 1 : row.RepeatCount),

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
