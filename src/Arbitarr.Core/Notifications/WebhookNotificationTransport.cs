using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace Arbitarr.Core.Notifications;

/// <summary>
/// Posts a notification to the operator's configured webhook, classified into a closed
/// <see cref="NotificationDeliveryOutcome"/>. The direct counterpart of
/// <see cref="Sources.SourceConnectivityProber"/>, deliberately built to the same shape so the two
/// outbound-request surfaces in this codebase behave and fail identically.
///
/// <para><b>THE URL IS A SECRET AND NEVER COMES BACK OUT.</b> Discord, Telegram, Gotify and
/// Notifiarr all carry the token in the URL path, so this type treats the whole URL the way
/// <c>SourceConnectivityProber</c> treats an API key: it is read, used in exactly one outbound
/// request, and never returned, logged, or interpolated into anything. The return type is a bare
/// enum, so there is no field that could carry it; exception messages (which routinely embed the
/// host — see <c>SanitizedErrorDescription</c>'s note) are classified structurally by exception
/// TYPE and then discarded, never read for text.</para>
///
/// <para><b>SSRF: the target is operator-supplied and deliberately unrestricted, with the redirect
/// hole closed.</b> A notification webhook is an outbound request to a URL the operator typed, so
/// it IS a request-forgery surface by definition. It is not treated as an untrusted-input SSRF the
/// way an attacker-supplied URL would be, and that is a reasoned position rather than an omission:
/// the URL can only be set through the <c>AdminMutating</c> gate by someone holding the admin key,
/// the whole point of the feature is to reach a host on the operator's own LAN (Gotify, ntfy and
/// Home Assistant all run at RFC1918 addresses), and a private-range blocklist would therefore
/// break the primary use case while stopping nobody who already has admin. What IS closed is the
/// part the operator did not choose: <b>redirects are disabled on this client</b> (in the Host's
/// registration, the same as the source probe's), so a webhook endpoint cannot 30x this process
/// into issuing a request at an address the operator never configured. The response body is never
/// read, so an endpoint cannot use the reply as an exfiltration channel either — the only thing
/// that flows back is one enum member.</para>
///
/// <para><b>Best-effort, never blocking (§3.4 / AC6).</b> A short timeout, no retry, and every
/// reachable-world failure is an outcome rather than an exception. A caller cannot be slowed by
/// more than <see cref="DefaultTimeout"/> and cannot be failed at all.</para>
/// </summary>
public sealed class WebhookNotificationTransport
{
    /// <summary>
    /// The delivery timeout. Short because §3.4 forbids the notifier delaying anything: a webhook
    /// host that has gone away must fail while the operator is still looking at the test button,
    /// and must never hold a background cycle open.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public WebhookNotificationTransport(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Posts <paramref name="payload"/> as JSON to <paramref name="webhookUrl"/>.
    ///
    /// Never throws for a delivery-side failure, and never surfaces the URL or the endpoint's
    /// response. Cancellation the CALLER requested is rethrown rather than classified, so a host
    /// shutdown is not misreported as a broken webhook.
    /// </summary>
    /// <param name="webhookUrl">The operator's configured URL. Null or empty reports <see cref="NotificationDeliveryOutcome.NotConfigured"/>.</param>
    /// <param name="payload">What to send.</param>
    public async Task<NotificationDeliveryOutcome> DeliverAsync(
        string? webhookUrl,
        NotificationPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)
            || !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            // A malformed stored URL is reported as unconfigured rather than as a distinct
            // "invalid" outcome, because the operator's next action is the same (set a valid URL)
            // and a separate outcome would invite a message quoting the offending value.
            return NotificationDeliveryOutcome.NotConfigured;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var content = new StringContent(Serialize(payload), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = content };

            // ResponseHeadersRead, NOT the PostAsync default. PostAsync implies
            // HttpCompletionOption.ResponseContentRead, which BUFFERS the entire response body
            // before returning — so an operator-supplied endpoint that streamed an unbounded reply
            // would be read into host memory even though nothing here wants the body. Only the
            // status line is needed, so only the headers are awaited. (A test asserting the body is
            // never read is what caught this; the same explicit-completion-option care is taken by
            // SourceConnectivityProber.)
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            // The body is deliberately never read. Nothing downstream needs it, and not reading it
            // means no endpoint-controlled text can ever reach a log, a response, or a stored
            // last-result — see the type's SSRF note.
            return response.IsSuccessStatusCode
                ? NotificationDeliveryOutcome.Delivered
                : NotificationDeliveryOutcome.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away (host shutting down). Not a verdict about the webhook.
            throw;
        }
        catch (OperationCanceledException)
        {
            return NotificationDeliveryOutcome.Unreachable;
        }
        catch (HttpRequestException ex)
        {
            return Classify(ex);
        }
    }

    /// <summary>
    /// The documented payload shape (AC3): a small, stable JSON object. Field names are fixed and
    /// generic rather than shaped for one provider, which is what lets a single transport reach
    /// Gotify, ntfy, Discord, Slack and a homegrown receiver without per-provider code.
    ///
    /// Note what is NOT in it: no URL, no id, no credential, no free text derived from an exception
    /// or an upstream response. Everything here originates in <see cref="NotificationPolicy"/>,
    /// which never sees a secret at all.
    /// </summary>
    private static string Serialize(NotificationPayload payload) => JsonSerializer.Serialize(new
    {
        source = "arbitarr",
        trigger = ToCamelCase(payload.Trigger.ToString()),
        title = "Arbitarr",
        message = payload.Summary,
        sourceName = payload.SourceDisplayName,
        occurredAt = payload.OccurredAt,
    });

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    /// <summary>
    /// Separates a TLS failure from an ordinary connection failure, structurally. Identical in
    /// approach to <c>SourceConnectivityProber.Classify</c>: walk the inner-exception chain for an
    /// <see cref="AuthenticationException"/> (TLS handshake) or a <see cref="SocketException"/>
    /// (DNS, refused, no route), then fall back to .NET's own <see cref="HttpRequestError"/>
    /// classification. The message text is NEVER inspected — it is localised, platform-specific,
    /// and routinely embeds the very URL this type exists to keep secret.
    /// </summary>
    private static NotificationDeliveryOutcome Classify(HttpRequestException exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return NotificationDeliveryOutcome.TlsFailure;
            }

            if (inner is SocketException)
            {
                return NotificationDeliveryOutcome.Unreachable;
            }
        }

        return exception.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => NotificationDeliveryOutcome.TlsFailure,
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError =>
                NotificationDeliveryOutcome.Unreachable,
            // No usable answer arrived and we cannot attribute it more precisely; "check the
            // address" is the honest next step, so report it as unreachable rather than as a
            // rejection the endpoint never issued.
            _ => NotificationDeliveryOutcome.Unreachable,
        };
    }
}
