namespace Arbitarr.Core.Notifications;

/// <summary>
/// Which of the four notification-worthy conditions produced a notification (#57's AC "the four
/// event types above are emitted", and the per-type enable/disable AC hangs off exactly this set).
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
