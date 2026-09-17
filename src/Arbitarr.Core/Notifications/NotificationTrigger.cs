namespace Arbitarr.Core.Notifications;

/// <summary>
/// Which notification-worthy condition produced a notification (#57's AC "the four event types
/// above are emitted", and the per-type enable/disable AC hangs off exactly this set — which has
/// since grown past those four, most recently with the permanent-disable pair arb-rx1f adds).
///
/// These are NOT event kinds and must not be confused with <see cref="Diagnostics.RecordedEventKind"/>.
/// An event kind describes a row the store already holds; a trigger describes a CONCLUSION the
/// policy reached by reading many such rows — "this source has now failed N times consecutively",
/// "the suppression rate over the window crossed the threshold". That distinction is why #57 adds
/// no <c>EventKind</c> member: nothing new is recorded, something existing is interpreted.
/// </summary>
public enum NotificationTrigger
{
    /// <summary>A source crossed the consecutive-failure threshold into the failed state.</summary>
    SourceFailing,

    /// <summary>A source that had been notified as failing answered successfully again.</summary>
    SourceRecovered,

    /// <summary>The suppression rate over the evaluation window crossed the configured threshold.</summary>
    SuppressionRateHigh,

    /// <summary>
    /// The suppression rate fell back below the threshold. The closing edge of
    /// <see cref="SuppressionRateHigh"/>, for the same reason source recovery exists: an operator
    /// who is told a rate is high and never told it came down cannot distinguish a still-broken
    /// rule from a fixed one without going and looking, which is the problem this feature removes.
    /// </summary>
    SuppressionRateNormal,

    /// <summary>
    /// A source began refusing downloads with a redirect (arb-apj), raising the sticky health item
    /// arb-ln0 added to <c>/api/status</c>.
    ///
    /// <para><b>This trigger exists precisely BECAUSE the condition must not reach
    /// <see cref="SourceFailing"/>.</b> A refused redirect is a configuration answer from a HEALTHY
    /// upstream that repeats on every download until the operator changes a setting, so folding it
    /// through <c>NotificationPolicy.FoldSourceFailure</c>'s consecutive-failure counter would
    /// announce a working source as down after three of Sonarr's retries and then clear it on an
    /// unrelated worker cycle — the defect <c>DownloadProxyEndpoint</c>'s catch block documents at
    /// length and deliberately avoids by leaving the event's source name null. A separate trigger
    /// on a separate transition keeps that avoidance intact while still telling the operator.</para>
    /// </summary>
    DownloadRefused,

    /// <summary>
    /// A source that had been refusing downloads served one successfully again, clearing its health
    /// item. The closing edge of <see cref="DownloadRefused"/>, for the reason
    /// <see cref="SuppressionRateNormal"/> states: an operator told a condition started and never
    /// told it ended has to go and look, which is what the notification exists to save them.
    /// </summary>
    DownloadRefusalCleared,

    /// <summary>
    /// A source's API key was rejected by the upstream (a 401 or a 403), so
    /// <c>SourceBackoffStore.RecordOutcomeAsync</c> set its permanent-disable flag and every later
    /// search skips it (arb-rx1f).
    ///
    /// <para><b>This trigger exists because the permanently-disabled condition has no edge anywhere
    /// else to hang a notification on.</b> The <c>/api/status</c> health item for it is PROJECTED
    /// FROM THE STORED ROW AT READ TIME — deliberately, because no clock may expire a rejected key —
    /// so nothing polls it and no tracker holds it. There is therefore no equivalent of
    /// <c>NotifyingDownloadRefusalTracker</c> to decorate, and a read-time projection cannot tell an
    /// operator anything until they go and look, which is precisely what a notification is for. The
    /// one moment the transition is observable is the record itself, where the before-state and the
    /// after-state are both in hand.</para>
    ///
    /// <para><b>It must not be folded into <see cref="SourceFailing"/>.</b> That trigger is reached
    /// by <c>NotificationPolicy</c>'s consecutive-failure counter over event rows, which is a count
    /// of blips; a rejected key is not a blip and does not recover on its own, so counting it would
    /// both delay the notice by the threshold and let an unrelated success clear it while the key was
    /// still rejected. A condition that persists until an operator changes a setting is the same
    /// shape as <see cref="DownloadRefused"/>, and it gets the same treatment: its own trigger on its
    /// own transition.</para>
    /// </summary>
    SourcePermanentlyDisabled,

    /// <summary>
    /// A source whose key had been rejected answered successfully again, clearing its permanent
    /// disable. The closing edge of <see cref="SourcePermanentlyDisabled"/>, for the reason
    /// <see cref="SuppressionRateNormal"/> and <see cref="DownloadRefusalCleared"/> both state: an
    /// operator told a condition started and never told it ended has to go and look.
    ///
    /// <para>The proving event is a real success against upstream. A <c>NotAttempted</c> outcome
    /// deliberately clears nothing — see <c>SourceBackoffStore.RecordOutcomeAsync</c> — so this
    /// cannot fire merely because a breaker opened.</para>
    /// </summary>
    SourcePermanentlyDisabledCleared,
}

/// <summary>
/// One notification the policy decided to send, in the shape the transport posts.
///
/// <para><b>THERE IS NO URL, ID, OR ENDPOINT FIELD HERE, AND THAT IS DELIBERATE.</b> A payload is
/// built by <see cref="NotificationPolicy"/>, which never sees the webhook URL — the transport
/// reads that separately and joins the two at the moment of the POST. So no payload, log line, or
/// test fixture can carry the target even by accident, and the "URL reaches no event row" property
/// is not something the policy has to remember.</para>
/// </summary>
/// <param name="Trigger">Which condition fired.</param>
/// <param name="Summary">One line an operator reads in their notification client.</param>
/// <param name="SourceDisplayName">
/// The source involved for the two source triggers, else null. A configured display name, never a
/// credential — the same rule <c>EventEntry.SourceDisplayName</c> states, and the values here are
/// read straight off event rows that already passed that repository's write-boundary validation.
/// </param>
/// <param name="OccurredAt">When the policy reached this conclusion.</param>
public readonly record struct NotificationPayload(
    NotificationTrigger Trigger,
    string Summary,
    string? SourceDisplayName,
    DateTimeOffset OccurredAt);
