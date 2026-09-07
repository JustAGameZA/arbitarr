namespace Arbitarr.Core.Notifications;

/// <summary>
/// The result of one attempted webhook delivery, as a CLOSED set of outcomes rather than a boolean
/// or a message string — the same shape, for the same reason, as
/// <see cref="Sources.SourceProbeOutcome"/>.
///
/// <para><b>Why a closed enum and not an error string, restated for this feature.</b> The probe's
/// argument was that its outcome must not carry text derived from the API key it sends upstream.
/// Here the argument is sharper: for a notification webhook <i>the URL itself is the secret</i>.
/// Discord, Telegram, Notifiarr and Gotify all put the token in the URL path, so an exception
/// message ("No such host is known (hooks.example.com)"), a response body, or a
/// <see cref="Uri"/> echoed into a failure string is a credential disclosure — and #57's config
/// surface, its test button, and its logs would each be a separate place to remember to sanitise.
/// A closed enum makes there be no field capable of carrying it, so the property holds by
/// construction rather than by care. This type is deliberately the ONLY thing a delivery reports.
/// </para>
///
/// <para><b>Why these outcomes.</b> Each is a different thing the operator does next, which is what
/// §3.3 demands of the test button ("report the real transport error"): a wrong address, a bad
/// certificate, an endpoint that refuses the post, and an endpoint that accepted nothing useful are
/// four different fixes. <see cref="Rejected"/> deliberately merges every non-success status rather
/// than reporting the code, because the code is the one piece of upstream-derived data most likely
/// to be interpolated into a message next to the URL that produced it.</para>
/// </summary>
public enum NotificationDeliveryOutcome
{
    /// <summary>The endpoint accepted the POST with a success status. Delivery worked.</summary>
    Delivered,

    /// <summary>
    /// No usable connection: DNS failure, connection refused, no route, or the delivery exceeded
    /// its short timeout. The URL's host or port is wrong, or the service is down.
    /// </summary>
    Unreachable,

    /// <summary>
    /// A connection was made but the TLS handshake failed — an untrusted or expired certificate, a
    /// hostname mismatch, or https pointed at a plaintext port. Distinct from
    /// <see cref="Unreachable"/> because the address is right and the transport is the problem.
    /// </summary>
    TlsFailure,

    /// <summary>
    /// The endpoint answered and refused the notification (any non-success status). Usually a
    /// revoked or mistyped token in the URL, or a payload the endpoint will not accept.
    /// </summary>
    Rejected,

    /// <summary>
    /// No webhook URL is configured, so there was nothing to deliver to. Reported as an outcome
    /// rather than thrown: an unconfigured notifier is an ordinary state on a fresh install, not a
    /// fault, and the test button must be able to say so plainly.
    /// </summary>
    NotConfigured,
}
