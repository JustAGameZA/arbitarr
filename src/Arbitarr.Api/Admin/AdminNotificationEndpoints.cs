using Arbitarr.Core.Notifications;
using Arbitarr.Data.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// The notification configuration as served over the wire. Note what is absent: there is no field
/// for the webhook URL, and deliberately no nullable "url" property that a future edit could start
/// populating. Presence is reported by <paramref name="HasWebhookUrl"/> and nothing else — the same
/// structural guarantee <see cref="SourceResponse"/> makes about source API keys, and for a
/// stronger reason, since for a webhook the URL IS the credential.
/// </summary>
/// <param name="LastDeliveryOutcome">
/// The last delivery attempt's outcome as a stable enum name, or null if nothing has been
/// attempted. §3.4/AC6: "a notifier that is silently broken is worse than none". Never an exception
/// string or a status code — <see cref="NotificationDeliveryOutcome"/> is closed precisely so this
/// field cannot carry text derived from the target or its response.
/// </param>
/// <param name="AvailableTriggers">
/// Every <see cref="NotificationTrigger"/> member name, unfiltered by which are enabled (arb-4xna).
/// The client renders its checkbox list from THIS rather than from a hand-maintained local array, so
/// a trigger added to the enum in future shows up on the page (and therefore in the next save's
/// <c>enabledTriggers</c>) the moment the server ships it, instead of silently landing in the
/// persisted disabled set the way <see cref="NotificationRepository.SetSettingsAsync"/>'s
/// complement-of-enabled storage would otherwise do to a client that has not caught up.
/// </param>
public sealed record NotificationConfigResponse(
    bool Enabled,
    bool HasWebhookUrl,
    int ConsecutiveFailureThreshold,
    double SuppressionRateThreshold,
    string SuppressionRateWindow,
    IReadOnlyList<string> EnabledTriggers,
    IReadOnlyList<string> AvailableTriggers,
    string? LastDeliveryOutcome,
    DateTimeOffset? LastDeliveryAt);

/// <summary>
/// Request body for <c>PUT /api/admin/notifications</c>.
///
/// <paramref name="WebhookUrl"/> is write-only and optional, with the source-API-key semantics:
/// <b>null leaves the stored URL untouched</b>, a non-empty value replaces it. Omitting the field
/// must never clear it — the client never had the value, so it cannot "read and reapply" one, and
/// treating omission as a clear would make an ordinary threshold edit silently destroy the
/// operator's target. Clearing is <c>DELETE /api/admin/notifications/webhook</c>, explicitly.
/// </summary>
/// <param name="KnownTriggers">
/// arb-4xna: the full set of trigger names the CLIENT believes exist — not which are enabled,
/// <paramref name="EnabledTriggers"/> is that. Required whenever <paramref name="EnabledTriggers"/>
/// is sent, and checked only for its COUNT against the server's current
/// <see cref="NotificationTrigger"/> member count.
///
/// <para><see cref="NotificationRepository.SetSettingsAsync"/> persists the DISABLED set as the
/// complement of <paramref name="EnabledTriggers"/>, precisely so a trigger added to the enum after
/// an operator's last save defaults to enabled rather than muted. That guarantee depends on every
/// save actually knowing about every current trigger: a client built before a trigger existed has no
/// way to include it in <paramref name="EnabledTriggers"/>, and without this field the very save
/// meant to leave it alone would instead compute it into the complement and disable it — silently,
/// with no error. Comparing <paramref name="EnabledTriggers"/>'s own count against the enum would not
/// catch this, because an operator legitimately unchecking every box produces exactly that same
/// short count; only a count the client asserts is "everything I know about", separate from "what I
/// have enabled", tells the two apart.</para>
/// </param>
public sealed record UpdateNotificationConfigRequest(
    bool? Enabled,
    string? WebhookUrl,
    int? ConsecutiveFailureThreshold,
    double? SuppressionRateThreshold,
    string? SuppressionRateWindow,
    IReadOnlyList<string>? EnabledTriggers,
    IReadOnlyList<string>? KnownTriggers);

/// <summary>The outcome of <c>POST /api/admin/notifications/test</c>.</summary>
/// <param name="Outcome">
/// One of <see cref="NotificationDeliveryOutcome"/>, as a stable string the UI switches on.
/// </param>
/// <param name="Message">
/// Fixed, human-readable wording chosen from <paramref name="Outcome"/> alone. Never derived from
/// the endpoint's response, an exception message, or the configured URL.
/// </param>
public sealed record NotificationTestResponse(bool Success, string Outcome, string Message);

/// <summary>
/// #57's admin-gated notification configuration surface, plus the test affordance (§3.3).
/// Every route is <c>.RequireAdminApiKey()</c> — the gate is by path prefix, never by verb, so the
/// read (<c>GET</c>) is gated exactly like the writes. That is not merely consistency here: the
/// read carries the notifier's configuration, and while it deliberately cannot carry the URL
/// itself, its thresholds and last-delivery state are operator configuration rather than dashboard
/// facts.
///
/// <para><b>THE SECRET RULE, and how this file keeps it.</b> The webhook URL lives as a write-only
/// row in the Settings table under <c>notification:webhook_url</c> — a colon-namespaced name that
/// no <c>SettingKey</c> enum value can produce, so it can never surface on
/// <c>GET /api/admin/settings</c> (which projects from <c>SettingsCatalog.Entries</c>, never from
/// the table). This file preserves that structurally rather than by care:
/// <see cref="NotificationConfigResponse"/> has no field capable of carrying a URL,
/// <see cref="ToResponseAsync"/> is the single projection every read path goes through, and its
/// indicator comes from <see cref="NotificationRepository.HasWebhookUrlAsync"/>, which returns a
/// bool. The test route returns a closed enum and fixed wording. No response path in this file can
/// emit the URL.</para>
///
/// <para><b>THE REQUIRED-BODY TRAP.</b> Every body here is bound OPTIONALLY
/// (<c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>) and null-checked inside the
/// handler, exactly as <see cref="AdminSourceEndpoints"/> and <see cref="AdminSecurityEndpoints"/>
/// do, and for exactly the same reason: a REQUIRED body is model-bound BEFORE endpoint filters run,
/// so a missing or malformed one short-circuits to 400 without <see cref="AdminApiKeyFilter"/> ever
/// executing. An unauthenticated remote caller could then tell a malformed body (400) from a
/// well-formed one (503) and enumerate which admin routes exist from outside the gate. Binding
/// optionally keeps the gate strictly first. <c>AdminApiKeyRouteEnumerationTests</c> asserts this by
/// sweeping every concrete admin-mutating route sending NO body at all — that bodilessness is
/// load-bearing and must not be "fixed".</para>
///
/// <para><b>Validation is not duplicated here.</b>
/// <see cref="NotificationSettingsValidator"/> owns the bounds and the reject-never-clamp posture;
/// this layer only translates <see cref="NotificationSettingsValidationException"/> into a 400. One
/// validation floor, in one place.</para>
/// </summary>
public static class AdminNotificationEndpoints
{
    public const string NotificationsRoute = "/api/admin/notifications";

    /// <summary>The explicit "forget the configured target" route — see <see cref="UpdateNotificationConfigRequest"/>.</summary>
    public const string WebhookRoute = $"{NotificationsRoute}/webhook";

    /// <summary>§3.3's test affordance.</summary>
    public const string TestRoute = $"{NotificationsRoute}/test";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(NotificationsRoute, GetConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapPut(NotificationsRoute, UpdateConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapDelete(WebhookRoute, ClearWebhookAsync)
            .RequireAdminApiKey();

        endpoints.MapPost(TestRoute, SendTestAsync)
            .RequireAdminApiKey();
    }

    private static async Task<IResult> GetConfigAsync(
        NotificationRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await ToResponseAsync(repository, cancellationToken));

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> UpdateConfigAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateNotificationConfigRequest? request,
        NotificationRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with the notification configuration is required." });
        }

        var current = await repository.GetSettingsAsync(cancellationToken);

        TimeSpan window;
        if (request.SuppressionRateWindow is null)
        {
            window = current.SuppressionRateWindow;
        }
        else if (!TimeSpan.TryParse(request.SuppressionRateWindow, System.Globalization.CultureInfo.InvariantCulture, out window))
        {
            return Results.BadRequest(new { error = "Suppression rate window must be a duration such as '01:00:00'." });
        }

        IReadOnlySet<NotificationTrigger> triggers;
        if (request.EnabledTriggers is null)
        {
            triggers = current.EnabledTriggers;
        }
        else
        {
            var parsed = new HashSet<NotificationTrigger>();
            foreach (var name in request.EnabledTriggers)
            {
                if (!Enum.TryParse<NotificationTrigger>(name, ignoreCase: true, out var trigger))
                {
                    // The rejected NAME is echoed, which is safe and useful: trigger names are a
                    // fixed, public vocabulary, not operator secrets. The URL is the only value on
                    // this surface that may never appear in a response, and it is never echoed by
                    // any branch here.
                    return Results.BadRequest(new { error = $"Unknown notification trigger '{name}'." });
                }

                parsed.Add(trigger);
            }

            // arb-4xna: see UpdateNotificationConfigRequest.KnownTriggers. A save that names any
            // EnabledTriggers at all must also declare the full universe it believes exists, and that
            // universe's COUNT must cover every trigger the server currently has — otherwise the
            // repository's complement-of-enabled storage would silently disable whichever trigger the
            // client did not know to name. Checked by count alone: no trigger NAME beyond what the
            // per-name loop above already allows is required to explain the rejection.
            var knownTriggerCount = Enum.GetValues<NotificationTrigger>().Length;
            if (request.KnownTriggers is null || request.KnownTriggers.Count < knownTriggerCount)
            {
                return Results.BadRequest(new
                {
                    error = $"A request that sets enabledTriggers must also send knownTriggers naming all {knownTriggerCount} " +
                        "current triggers, so the server can tell an operator's real choice from a client that predates a newer trigger.",
                });
            }

            triggers = parsed;
        }

        var proposed = new NotificationSettings(
            Enabled: request.Enabled ?? current.Enabled,
            ConsecutiveFailureThreshold: request.ConsecutiveFailureThreshold ?? current.ConsecutiveFailureThreshold,
            SuppressionRateThreshold: request.SuppressionRateThreshold ?? current.SuppressionRateThreshold,
            SuppressionRateWindow: window,
            EnabledTriggers: triggers);

        try
        {
            // A whitespace-only URL is normalized to null (leave alone) rather than rejected: the
            // UI sends an empty field for "I did not change this", and treating that as a validation
            // failure would make every threshold edit require retyping the secret.
            var webhookUrl = string.IsNullOrWhiteSpace(request.WebhookUrl) ? null : request.WebhookUrl;
            await repository.SetSettingsAsync(proposed, webhookUrl, cancellationToken);
        }
        catch (NotificationSettingsValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.Ok(await ToResponseAsync(repository, cancellationToken));
    }

    /// <summary>
    /// Forgets the configured webhook target. Takes no body — the route names the whole action —
    /// so there is no model binding to run ahead of the admin filter.
    /// </summary>
    private static async Task<IResult> ClearWebhookAsync(
        NotificationRepository repository,
        CancellationToken cancellationToken)
    {
        await repository.ClearWebhookUrlAsync(cancellationToken);
        return Results.Ok(await ToResponseAsync(repository, cancellationToken));
    }

    /// <summary>
    /// §3.3's test notification: a REAL POST to the configured webhook, reporting the real
    /// transport outcome. Takes no body — the target is read from the database, so there is nothing
    /// for a caller to submit, and no model binding runs ahead of the admin filter.
    ///
    /// The result is recorded as the last delivery outcome exactly as a live notification would be,
    /// so the operator's test and the notifier's own health read the same field rather than
    /// diverging. The stored URL is read here and handed to the transport; it never enters the
    /// response, because <see cref="NotificationDeliveryOutcome"/> is a closed enum and
    /// <see cref="DescribeOutcome"/> maps it to fixed wording.
    /// </summary>
    private static async Task<IResult> SendTestAsync(
        NotificationRepository repository,
        WebhookNotificationTransport transport,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var url = await repository.ReadWebhookUrlForDeliveryAsync(cancellationToken);

        var payload = new NotificationPayload(
            NotificationTrigger.SourceRecovered,
            "Test notification from Arbitarr. If you can read this, notifications are configured correctly.",
            SourceDisplayName: null,
            timeProvider.GetUtcNow());

        var outcome = await transport.DeliverAsync(url, payload, cancellationToken);
        await repository.RecordDeliveryAsync(outcome, cancellationToken);

        return Results.Ok(new NotificationTestResponse(
            Success: outcome == NotificationDeliveryOutcome.Delivered,
            Outcome: outcome.ToString(),
            Message: DescribeOutcome(outcome)));
    }

    /// <summary>
    /// Fixed operator-facing wording per outcome. Each says what to check next, which is why §3.3
    /// demands distinct outcomes instead of one red "failed". Sourced only from the enum — never
    /// from the endpoint's response or the configured URL.
    /// </summary>
    private static string DescribeOutcome(NotificationDeliveryOutcome outcome) => outcome switch
    {
        NotificationDeliveryOutcome.Delivered =>
            "The webhook accepted the test notification.",
        NotificationDeliveryOutcome.Unreachable =>
            "Could not reach the webhook: no response from that address before the timeout. Check the host and port in the URL, and that the service is running.",
        NotificationDeliveryOutcome.TlsFailure =>
            "Reached the webhook but the TLS handshake failed. Check the certificate (expired, self-signed, or issued for a different hostname) or use http if the service is not serving TLS on that port.",
        NotificationDeliveryOutcome.Rejected =>
            "The webhook is reachable but refused the notification. The token in the URL may have been revoked, or the endpoint may not accept this payload.",
        NotificationDeliveryOutcome.NotConfigured =>
            "No webhook URL is configured, so there was nothing to send a test to. Set one and try again.",
        _ => "The test notification did not complete.",
    };

    /// <summary>
    /// The SINGLE projection to the wire. Every read path goes through here, so the "no URL ever
    /// leaves" property is enforced in one place: the URL is represented only by the boolean from
    /// <see cref="NotificationRepository.HasWebhookUrlAsync"/>, and
    /// <see cref="NotificationConfigResponse"/> has no field that could hold the value even if a
    /// future caller tried.
    /// </summary>
    private static async Task<NotificationConfigResponse> ToResponseAsync(
        NotificationRepository repository,
        CancellationToken cancellationToken)
    {
        var settings = await repository.GetSettingsAsync(cancellationToken);
        var lastDelivery = await repository.GetLastDeliveryAsync(cancellationToken);

        return new NotificationConfigResponse(
            Enabled: settings.Enabled,
            HasWebhookUrl: await repository.HasWebhookUrlAsync(cancellationToken),
            ConsecutiveFailureThreshold: settings.ConsecutiveFailureThreshold,
            SuppressionRateThreshold: settings.SuppressionRateThreshold,
            SuppressionRateWindow: settings.SuppressionRateWindow.ToString(),
            EnabledTriggers: settings.EnabledTriggers.Select(t => t.ToString()).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            AvailableTriggers: Enum.GetValues<NotificationTrigger>().Select(t => t.ToString()).ToList(),
            LastDeliveryOutcome: lastDelivery?.Outcome.ToString(),
            LastDeliveryAt: lastDelivery?.At);
    }
}
