namespace Arbitarr.Core.Notifications;

/// <summary>
/// Thrown when proposed notification settings fail validation at the repository boundary. Mirrors
/// <see cref="Settings.SettingsValidationException"/> and <c>SourceValidationException</c>: reject
/// with a clear reason, never silently coerce or clamp (AC24).
/// </summary>
public sealed class NotificationSettingsValidationException : Exception
{
    public NotificationSettingsValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Bounds for #57's tunables, with the reason each bound exists — the same posture
/// <see cref="Settings.SettingsValidator"/> takes, and for the same AC24 reason: an operator is
/// never shown a bare, unexplained rejection.
/// </summary>
public static class NotificationSettingsValidator
{
    /// <summary>
    /// Lowest consecutive-failure threshold. Two, not one: a threshold of one notifies on every
    /// single transient blip, which is precisely the notification fatigue §3.1 exists to prevent,
    /// and an operator who wants that can have it by watching the Activity surface instead.
    /// </summary>
    public const int MinimumConsecutiveFailureThreshold = 2;

    /// <summary>
    /// Highest consecutive-failure threshold. Above this a genuinely dead source stays unreported
    /// for so long that the feature has stopped doing its job.
    /// </summary>
    public const int MaximumConsecutiveFailureThreshold = 50;

    /// <summary>Shortest rate window. Below this the sample is too small to be a rate at all.</summary>
    public static readonly TimeSpan MinimumSuppressionRateWindow = TimeSpan.FromMinutes(5);

    /// <summary>Longest rate window. Beyond a day, a rate stops tracking "what changed" and becomes an average.</summary>
    public static readonly TimeSpan MaximumSuppressionRateWindow = TimeSpan.FromDays(1);

    /// <summary>Validates every field, rejecting the whole settings object on the first failure.</summary>
    public static void Validate(NotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.ConsecutiveFailureThreshold < MinimumConsecutiveFailureThreshold
            || settings.ConsecutiveFailureThreshold > MaximumConsecutiveFailureThreshold)
        {
            throw new NotificationSettingsValidationException(
                $"Consecutive failure threshold must be between {MinimumConsecutiveFailureThreshold} and "
                + $"{MaximumConsecutiveFailureThreshold}; a threshold of one would notify on every transient blip.");
        }

        if (settings.SuppressionRateThreshold is < 0 or > 1 || double.IsNaN(settings.SuppressionRateThreshold))
        {
            throw new NotificationSettingsValidationException(
                "Suppression rate threshold must be a fraction between 0 and 1.");
        }

        if (settings.SuppressionRateWindow < MinimumSuppressionRateWindow
            || settings.SuppressionRateWindow > MaximumSuppressionRateWindow)
        {
            throw new NotificationSettingsValidationException(
                $"Suppression rate window must be between {MinimumSuppressionRateWindow} and {MaximumSuppressionRateWindow}.");
        }
    }

    /// <summary>
    /// Validates a proposed webhook URL's SHAPE only: absolute, and http or https.
    ///
    /// <para><b>The rejection message must never quote the value.</b> The URL is the secret (see
    /// <c>WebhookNotificationTransport</c>), and a validation error is a response body — echoing
    /// the rejected URL back would defeat the write-only storage it is about to be written into.
    /// The message therefore states the RULE, never the input, which is the same discipline
    /// <c>SourceProbeOutcome</c> enforces structurally.</para>
    ///
    /// <para><b>What this deliberately does NOT check is the host.</b> No private-range blocklist,
    /// no allow-list: the operator's Gotify or ntfy instance is at an RFC1918 address by design, so
    /// blocking those would break the feature's primary use case while stopping nobody — the URL
    /// can only be set by a caller who already holds the admin key. See
    /// <c>WebhookNotificationTransport</c>'s SSRF note for the full reasoning and for the part that
    /// IS closed (redirects).</para>
    /// </summary>
    public static void ValidateWebhookUrl(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            throw new NotificationSettingsValidationException("Webhook URL must not be empty.");
        }

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new NotificationSettingsValidationException(
                "Webhook URL must be an absolute http or https URL.");
        }
    }
}
