namespace Arbitarr.Core.Notifications;

/// <summary>
/// One observation the policy folds in: an event the store already recorded, reduced to only what
/// the policy needs. Deliberately not <c>EventEntry</c> — Core may not reference Arbitarr.Data
/// (<c>CoreIsolationTests</c>), and the reduction is useful in its own right, because it makes
/// visible that the policy reads a KIND, a SOURCE and a TIME and nothing else. No detail string,
/// no reason, no free text of any kind crosses into the policy, so nothing the policy emits can
/// carry content derived from them.
/// </summary>
/// <param name="Kind">Which of the observed conditions this row represents.</param>
/// <param name="SourceDisplayName">The source involved, or null.</param>
/// <param name="OccurredAt">When it happened.</param>
public readonly record struct NotificationObservation(
    ObservedEventKind Kind,
    string? SourceDisplayName,
    DateTimeOffset OccurredAt);

/// <summary>
/// The event kinds the notifier actually interprets, which is a strict subset of what the store
/// records. Mapping the store's kinds down to this set is the reader's job (Arbitarr.Host), so the
/// policy has no opinion about kinds it does not act on.
/// </summary>
public enum ObservedEventKind
{
    /// <summary>A source failed (the store's <c>SourceFailed</c>).</summary>
    SourceFailure,

    /// <summary>
    /// A source did something successfully — a snapshot refresh or a completed worker cycle. The
    /// recovery signal, since the store has no explicit "source recovered" row and does not need
    /// one: a source that produces a successful cycle is, by observation, answering again.
    /// </summary>
    SourceSuccess,

    /// <summary>A pipeline decision that ENFORCED a suppression (the store's <c>Decision</c>, shadow mode off).</summary>
    EnforcedSuppression,

    /// <summary>A pipeline decision that did not enforce (shadow-mode flagged). Counts in the denominator only.</summary>
    ShadowedSuppression,
}

/// <summary>
/// The notifier's durable memory: what it has already told the operator. Persisted between runs so
/// a restart does not re-notify (AC7).
/// </summary>
/// <param name="Cursor">
/// The last <c>EventEntry.Id</c> folded in. Ordering is by Id, never by OccurredAt, for the reason
/// <c>EventQuery.Cursor</c> documents at length: OccurredAt is non-unique (a burst shares one
/// instant) and a cursor on a non-unique key drops or repeats the ties.
/// </param>
/// <param name="FailingSources">Sources currently notified as failing, with their consecutive-failure counts.</param>
/// <param name="SuppressionRateHigh">Whether the suppression rate is currently in the notified-high state.</param>
public sealed record NotificationState(
    long? Cursor,
    IReadOnlyDictionary<string, int> FailingSources,
    bool SuppressionRateHigh)
{
    /// <summary>A notifier that has never run.</summary>
    public static NotificationState Empty { get; } =
        new(Cursor: null, FailingSources: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), SuppressionRateHigh: false);
}

/// <summary>
/// The outcome of folding one batch of observations into the state: the new state to persist, and
/// the notifications to deliver.
/// </summary>
/// <param name="State">The state after this batch. Persist it whether or not anything is delivered.</param>
/// <param name="Notifications">What to send, in order. Empty is the common case.</param>
public sealed record NotificationDecision(
    NotificationState State,
    IReadOnlyList<NotificationPayload> Notifications);

/// <summary>
/// #57's §3.1 policy: turns a stream of already-recorded events into the few notifications that
/// warrant an operator's attention, and remembers what it has said.
///
/// <para><b>This type performs no I/O and holds no state of its own.</b> It takes the prior state
/// and a batch of observations and returns the next state plus what to send. That is what makes
/// §3.1's central requirement — "both are stateful; a stateless notifier re-sends on every
/// evaluation" — testable exhaustively without a database, a clock, or an HTTP endpoint: every
/// edge in §5's test list is a pure function call.</para>
///
/// <para><b>THE TWO POLICIES ARE DIFFERENT IN KIND, which is the plan's central design constraint.</b>
/// A source failure is an infrastructure fault, so it notifies on the TRANSITION into and out of
/// failure — once each, never per failed request. A suppression is a routine pipeline decision, so
/// it notifies only when the RATE over a window crosses a threshold — never per suppression, which
/// would train the operator to ignore notifications within a day. Collapsing these into one
/// mechanism (e.g. "notify on the Nth of anything") would get the suppression case wrong.</para>
///
/// <para><b>RESTART BEHAVIOUR (AC7).</b> The state is persisted, so a restart resumes from the
/// stored cursor and the stored failing-source set. A source already notified as failing before the
/// restart stays in that set and is NOT re-notified; it will produce exactly one recovery
/// notification when it next succeeds. The cursor also means events recorded while the notifier was
/// down are folded in on the next run rather than skipped — so a source that failed and recovered
/// entirely during downtime produces its failure and recovery notifications late rather than not at
/// all. The alternative (resetting the cursor to "now" at startup) would silently drop conditions
/// the operator never heard about, which is the worse failure for a feature whose whole purpose is
/// to tell them.</para>
/// </summary>
public sealed class NotificationPolicy
{
    private readonly NotificationSettings _settings;

    public NotificationPolicy(NotificationSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Folds <paramref name="batch"/> into <paramref name="prior"/> and reports what to send.
    ///
    /// <paramref name="batch"/> must be in ascending event order (oldest first) — the transition
    /// detection is a fold and reordering it would change the answer. <paramref name="cursor"/> is
    /// the highest event id in the batch, carried through to the returned state so the reader
    /// resumes strictly after it.
    /// </summary>
    /// <param name="prior">The state from the previous run, or <see cref="NotificationState.Empty"/>.</param>
    /// <param name="batch">Observations, oldest first.</param>
    /// <param name="cursor">Highest event id folded in, or null to leave the prior cursor.</param>
    /// <param name="now">Current time, for the rate window's lower bound.</param>
    public NotificationDecision Evaluate(
        NotificationState prior,
        IReadOnlyList<NotificationObservation> batch,
        long? cursor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(batch);

        var failing = new Dictionary<string, int>(prior.FailingSources, StringComparer.OrdinalIgnoreCase);
        var notifications = new List<NotificationPayload>();

        foreach (var observation in batch)
        {
            switch (observation.Kind)
            {
                case ObservedEventKind.SourceFailure:
                    FoldSourceFailure(observation, failing, notifications);
                    break;

                case ObservedEventKind.SourceSuccess:
                    FoldSourceSuccess(observation, failing, notifications);
                    break;

                // Suppression kinds are not folded one at a time: they are counted over the window
                // below, because the RATE is the signal and an individual suppression is not.
                case ObservedEventKind.EnforcedSuppression:
                case ObservedEventKind.ShadowedSuppression:
                    break;
            }
        }

        var rateHigh = EvaluateSuppressionRate(prior.SuppressionRateHigh, batch, now, notifications);

        return new NotificationDecision(
            new NotificationState(cursor ?? prior.Cursor, failing, rateHigh),
            notifications);
    }

    /// <summary>
    /// One recorded failure for a source. Increments its consecutive count and notifies ONLY on the
    /// evaluation that reaches the threshold — the count keeps climbing afterwards but produces
    /// nothing further, which is §5's "the N+1th does not re-notify".
    /// </summary>
    private void FoldSourceFailure(
        NotificationObservation observation,
        Dictionary<string, int> failing,
        List<NotificationPayload> notifications)
    {
        var source = observation.SourceDisplayName;
        if (string.IsNullOrWhiteSpace(source))
        {
            // A source failure with no source to name cannot be tracked as a transition or
            // meaningfully reported, so it is counted by nobody. Better than inventing a bucket.
            return;
        }

        var count = failing.TryGetValue(source, out var existing) ? existing : 0;
        count++;
        failing[source] = count;

        // Strictly equal, not >=: this is the edge into the failed state and fires exactly once.
        if (count != _settings.ConsecutiveFailureThreshold)
        {
            return;
        }

        if (!_settings.IsEnabled(NotificationTrigger.SourceFailing))
        {
            // Muted for delivery, but the state above was still updated, so re-enabling the trigger
            // does not resurrect a stale transition and recovery still works.
            return;
        }

        notifications.Add(new NotificationPayload(
            NotificationTrigger.SourceFailing,
            $"Source '{source}' has failed {count} times in a row and is being treated as down.",
            source,
            observation.OccurredAt));
    }

    /// <summary>
    /// A source answered. Clears its failure count, and notifies recovery only if the source had
    /// actually crossed the threshold — a source that blipped twice under a threshold of three was
    /// never reported as failing, so telling the operator it "recovered" would be reporting a
    /// problem they never had.
    /// </summary>
    private void FoldSourceSuccess(
        NotificationObservation observation,
        Dictionary<string, int> failing,
        List<NotificationPayload> notifications)
    {
        var source = observation.SourceDisplayName;
        if (string.IsNullOrWhiteSpace(source) || !failing.TryGetValue(source, out var count))
        {
            return;
        }

        failing.Remove(source);

        if (count < _settings.ConsecutiveFailureThreshold)
        {
            return;
        }

        if (!_settings.IsEnabled(NotificationTrigger.SourceRecovered))
        {
            return;
        }

        notifications.Add(new NotificationPayload(
            NotificationTrigger.SourceRecovered,
            $"Source '{source}' is answering again.",
            source,
            observation.OccurredAt));
    }

    /// <summary>
    /// Computes the suppression rate over the window and reports the two EDGES of the threshold,
    /// never the level. Returns the new high/normal state.
    ///
    /// The denominator is every decision in the window (enforced and shadow-flagged alike), because
    /// "half of what the pipeline decided was suppressed" is the operator's actual question;
    /// counting only suppressions would make the rate trivially 1.0 forever. Below
    /// <see cref="NotificationSettings.MinimumSuppressionSample"/> decisions the rate is not
    /// computed at all and the prior state is held — see that constant for why a small denominator
    /// is the noise source this whole feature is trying not to become.
    /// </summary>
    private bool EvaluateSuppressionRate(
        bool priorHigh,
        IReadOnlyList<NotificationObservation> batch,
        DateTimeOffset now,
        List<NotificationPayload> notifications)
    {
        var windowStart = now - _settings.SuppressionRateWindow;

        var decisions = 0;
        var enforced = 0;
        foreach (var observation in batch)
        {
            if (observation.OccurredAt < windowStart)
            {
                continue;
            }

            if (observation.Kind == ObservedEventKind.EnforcedSuppression)
            {
                enforced++;
                decisions++;
            }
            else if (observation.Kind == ObservedEventKind.ShadowedSuppression)
            {
                decisions++;
            }
        }

        if (decisions < NotificationSettings.MinimumSuppressionSample)
        {
            return priorHigh;
        }

        var rate = (double)enforced / decisions;
        var high = rate >= _settings.SuppressionRateThreshold;

        if (high == priorHigh)
        {
            // No edge. This is the branch that makes "staying above the threshold does not
            // re-notify every evaluation" true (§5), and it is the whole point of holding state.
            return high;
        }

        var trigger = high ? NotificationTrigger.SuppressionRateHigh : NotificationTrigger.SuppressionRateNormal;
        if (_settings.IsEnabled(trigger))
        {
            var percent = rate * 100;
            notifications.Add(new NotificationPayload(
                trigger,
                high
                    ? $"Suppression rate is {percent:F0}% of {decisions} decisions, at or above the configured threshold."
                    : $"Suppression rate has fallen back to {percent:F0}% of {decisions} decisions.",
                SourceDisplayName: null,
                now));
        }

        // The state transitions whether or not the trigger is muted, so a muted trigger does not
        // leave the notifier permanently convinced the rate is still high.
        return high;
    }
}
