using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Arbitarr.Core.Notifications;

namespace Arbitarr.Core.Tests;

/// <summary>
/// #57's webhook transport: the outcome set must be distinguishable (§3.3 — the test button has to
/// report the real transport error), delivery must be best-effort (§3.4 — never throw, never hang),
/// and <b>the URL must never come back out</b>, because for a notification webhook the URL is the
/// credential.
///
/// Every URL here is an obviously-fake <c>example.com</c> or RFC 5737 <c>192.0.2.x</c> form. No
/// fixture in this file may look like a real Discord/Telegram/Gotify webhook.
/// </summary>
public class WebhookNotificationTransportTests
{
    /// <summary>
    /// A URL shaped the way real providers shape them — token in the PATH — so the leak tests are
    /// testing the thing that actually leaks. Deliberately on example.com and obviously fake.
    /// </summary>
    private const string WebhookUrl = "https://example.com/hooks/placeholder-secret-token-value";

    private static readonly NotificationPayload Payload = new(
        NotificationTrigger.SourceFailing,
        "Source 'placeholder-source' has failed 3 times in a row and is being treated as down.",
        "placeholder-source",
        new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_success_status_is_Delivered()
    {
        var transport = TransportReturning(new HttpResponseMessage(HttpStatusCode.NoContent));

        Assert.Equal(NotificationDeliveryOutcome.Delivered, await transport.DeliverAsync(WebhookUrl, Payload));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_non_success_status_is_Rejected(HttpStatusCode status)
    {
        var transport = TransportReturning(new HttpResponseMessage(status));

        Assert.Equal(NotificationDeliveryOutcome.Rejected, await transport.DeliverAsync(WebhookUrl, Payload));
    }

    [Fact]
    public async Task A_socket_failure_is_Unreachable()
    {
        var transport = TransportThrowing(new HttpRequestException(
            "connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(NotificationDeliveryOutcome.Unreachable, await transport.DeliverAsync(WebhookUrl, Payload));
    }

    [Fact]
    public async Task A_TLS_handshake_failure_is_TlsFailure_and_not_merely_Unreachable()
    {
        // The address is RIGHT and the transport is the problem, so reporting "unreachable" would
        // send the operator to check the wrong thing.
        var transport = TransportThrowing(new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid.")));

        Assert.Equal(NotificationDeliveryOutcome.TlsFailure, await transport.DeliverAsync(WebhookUrl, Payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/hook")]
    public async Task An_absent_or_unusable_url_is_NotConfigured(string? url)
    {
        var transport = TransportReturning(new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal(NotificationDeliveryOutcome.NotConfigured, await transport.DeliverAsync(url, Payload));
    }

    [Fact]
    public void The_outcomes_are_genuinely_distinct()
    {
        // §3.3 is about DISTINGUISHING the modes. A refactor collapsing two of them would pass
        // every test above individually while destroying the property the plan asked for.
        var outcomes = new[]
        {
            NotificationDeliveryOutcome.Delivered,
            NotificationDeliveryOutcome.Unreachable,
            NotificationDeliveryOutcome.TlsFailure,
            NotificationDeliveryOutcome.Rejected,
            NotificationDeliveryOutcome.NotConfigured,
        };

        Assert.Equal(outcomes.Length, outcomes.Distinct().Count());
    }

    [Fact]
    public async Task Delivery_times_out_rather_than_blocking_the_caller()
    {
        // §3.4/AC6: a notification is best-effort and must never delay the operation that produced
        // it. An endpoint that never answers must still produce a verdict.
        var transport = new WebhookNotificationTransport(
            new HttpClient(new NeverRespondingHandler()),
            timeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(NotificationDeliveryOutcome.Unreachable, await transport.DeliverAsync(WebhookUrl, Payload));
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_rather_than_reported_as_a_webhook_failure()
    {
        // Host shutdown says nothing about the webhook, so it must not be recorded as a verdict
        // against it — which would otherwise show the operator a broken notifier that is fine.
        var transport = new WebhookNotificationTransport(new HttpClient(new NeverRespondingHandler()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.DeliverAsync(WebhookUrl, Payload, cancelled.Token));
    }

    [Fact]
    public async Task The_payload_is_posted_as_json_to_the_configured_url()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var transport = new WebhookNotificationTransport(new HttpClient(handler));

        await transport.DeliverAsync(WebhookUrl, Payload);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(WebhookUrl, handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task The_documented_payload_shape_carries_the_trigger_and_message()
    {
        // AC3: the payload shape is documented and stable, and generic rather than shaped for one
        // provider — that is what lets one transport reach Gotify, ntfy, Discord and a homegrown
        // receiver with no per-provider code.
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var transport = new WebhookNotificationTransport(new HttpClient(handler));

        await transport.DeliverAsync(WebhookUrl, Payload);

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;

        Assert.Equal("arbitarr", root.GetProperty("source").GetString());
        Assert.Equal("sourceFailing", root.GetProperty("trigger").GetString());
        Assert.Equal(Payload.Summary, root.GetProperty("message").GetString());
        Assert.Equal("placeholder-source", root.GetProperty("sourceName").GetString());
    }

    [Fact]
    public async Task The_webhook_url_never_appears_in_the_posted_payload()
    {
        // THE LEAK TEST. Providers embed the token in the URL PATH, so the URL is the secret. It
        // must reach the request LINE and nothing else — never the body, which a receiving service
        // logs, forwards, and renders.
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var transport = new WebhookNotificationTransport(new HttpClient(handler));

        await transport.DeliverAsync(WebhookUrl, Payload);

        // Non-vacuous: the secret really is the target of this delivery before we assert its
        // absence from the body.
        Assert.Equal(WebhookUrl, handler.LastRequest!.RequestUri!.ToString());
        Assert.DoesNotContain("placeholder-secret-token-value", handler.LastBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_failure_outcome_can_carry_the_url_because_the_outcome_is_a_bare_enum()
    {
        // The structural argument, asserted rather than assumed: every failure path returns a value
        // of an enum type, which has no field capable of holding a URL, a response body, or an
        // exception message. A future refactor to "return a result object with a message" would
        // fail here — which is the point.
        var transport = TransportThrowing(new HttpRequestException(
            // A real exception message routinely embeds the host, which is exactly the leak.
            $"No such host is known ({WebhookUrl})",
            new SocketException((int)SocketError.HostNotFound)));

        var outcome = await transport.DeliverAsync(WebhookUrl, Payload);

        Assert.Equal(NotificationDeliveryOutcome.Unreachable, outcome);
        Assert.IsType<NotificationDeliveryOutcome>(outcome);
        Assert.DoesNotContain("placeholder-secret-token-value", outcome.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_response_body_is_never_read_so_it_cannot_become_an_exfiltration_channel()
    {
        // The endpoint is operator-supplied and may be hostile-by-accident. Nothing downstream
        // needs its reply, so nothing reads it — which means no endpoint-controlled text can reach
        // a log, a response, or the stored last-delivery result.
        //
        // Asserted with content that FAULTS if anything reads it, rather than with a flag the test
        // sets: a flag would only prove that one read path was not taken, whereas a throwing body
        // fails the delivery outright if any path reads it at all.
        var transport = TransportReturning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ExplodingContent(),
        });

        Assert.Equal(NotificationDeliveryOutcome.Delivered, await transport.DeliverAsync(WebhookUrl, Payload));
    }

    private static WebhookNotificationTransport TransportReturning(HttpResponseMessage response) =>
        new(new HttpClient(new StubHandler(response)));

    private static WebhookNotificationTransport TransportThrowing(Exception exception) =>
        new(new HttpClient(new ThrowingHandler(exception)));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(_exception);
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    /// <summary>Captures the request and its body so the leak tests can inspect what was sent.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage response) => _response = response;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _response;
        }
    }

    /// <summary>
    /// A response body that faults on any attempt to read it. If the transport ever starts reading
    /// what the endpoint returned, the delivery fails and the test that asserts
    /// <see cref="NotificationDeliveryOutcome.Delivered"/> catches it.
    /// </summary>
    private sealed class ExplodingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new InvalidOperationException("The response body must never be read.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
