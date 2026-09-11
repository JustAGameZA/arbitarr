using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Arbitarr.Core.Sources;

namespace Arbitarr.Core.Tests;

/// <summary>
/// #53 stage 53c, plan §3.3 / AC4: the connectivity test must perform a REAL request and must
/// distinguish its four failure modes, because unreachable / TLS / auth / unexpected-shape have
/// entirely different fixes and a single red "failed" makes the button decorative.
///
/// Each of the five outcomes is pinned here separately — that is the point of the class. The
/// address used throughout is 192.0.2.x (RFC 5737 documentation range), never a real host.
/// </summary>
public class SourceConnectivityProberTests
{
    private const string BaseUrl = "http://192.0.2.10:5076";
    private const string CapsBody = """<?xml version="1.0" encoding="UTF-8"?><caps><categories /></caps>""";

    [Fact]
    public async Task A_caps_response_with_an_accepted_key_is_Ok()
    {
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CapsBody, Encoding.UTF8, "application/xml"),
        });

        Assert.Equal(SourceProbeOutcome.Ok, await prober.ProbeAsync(BaseUrl, "placeholder-key"));
    }

    [Fact]
    public async Task A_socket_failure_is_Unreachable()
    {
        // DNS failure / connection refused / no route all surface as a SocketException wrapped in
        // an HttpRequestException — the "check the address" case.
        var prober = ProberThrowing(new HttpRequestException(
            "connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(SourceProbeOutcome.Unreachable, await prober.ProbeAsync(BaseUrl, "placeholder-key"));
    }

    [Fact]
    public async Task A_TLS_handshake_failure_is_TlsFailure_and_not_merely_Unreachable()
    {
        // The distinction that matters most: the address is RIGHT and the transport is the problem,
        // so telling the operator "unreachable" would send them to check the wrong thing.
        var prober = ProberThrowing(new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid.")));

        Assert.Equal(SourceProbeOutcome.TlsFailure, await prober.ProbeAsync(BaseUrl, "placeholder-key"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_is_AuthenticationFailed(HttpStatusCode status)
    {
        // AC4's headline case, and the reason the probe cannot be a URL-shape check: the source is
        // reachable and healthy, and only the key is wrong.
        var prober = ProberReturning(new HttpResponseMessage(status));

        Assert.Equal(SourceProbeOutcome.AuthenticationFailed, await prober.ProbeAsync(BaseUrl, "wrong-placeholder-key"));
    }

    [Fact]
    public async Task A_login_page_instead_of_the_API_is_UnexpectedResponse()
    {
        // 200 OK carrying HTML: the classic "base URL points at a reverse proxy or the wrong
        // service" case. Reporting Ok here is exactly the false pass §3.3 forbids.
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Please sign in</body></html>", Encoding.UTF8, "text/html"),
        });

        Assert.Equal(SourceProbeOutcome.UnexpectedResponse, await prober.ProbeAsync(BaseUrl, "placeholder-key"));
    }

    [Fact]
    public async Task A_server_error_is_UnexpectedResponse_rather_than_Unreachable()
    {
        // A 500 means something answered: the address is right, so "unreachable" would mislead.
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Equal(SourceProbeOutcome.UnexpectedResponse, await prober.ProbeAsync(BaseUrl, "placeholder-key"));
    }

    [Fact]
    public void All_four_failure_modes_are_distinct_values()
    {
        // AC4 is about DISTINGUISHING the modes, so pin that the four are actually different from
        // each other and from Ok. A future refactor that collapsed two of them would pass every
        // test above individually while destroying the property the plan asked for.
        var outcomes = new[]
        {
            SourceProbeOutcome.Ok,
            SourceProbeOutcome.Unreachable,
            SourceProbeOutcome.TlsFailure,
            SourceProbeOutcome.AuthenticationFailed,
            SourceProbeOutcome.UnexpectedResponse,
        };

        Assert.Equal(outcomes.Length, outcomes.Distinct().Count());
    }

    [Fact]
    public async Task The_probe_times_out_rather_than_hanging_the_UI()
    {
        // §3.3: "a wrong host should fail in seconds, not hang the UI". A handler that never
        // completes must still produce a verdict, via the prober's own short timeout.
        var prober = new SourceConnectivityProber(
            new HttpClient(new NeverRespondingHandler()),
            timeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(SourceProbeOutcome.Unreachable, await prober.ProbeAsync(BaseUrl, "placeholder-key"));
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_rather_than_reported_as_a_source_failure()
    {
        // An aborted request (operator navigated away, host shutting down) says nothing about the
        // source, so it must not be recorded as a verdict against it.
        var prober = new SourceConnectivityProber(new HttpClient(new NeverRespondingHandler()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prober.ProbeAsync(BaseUrl, "placeholder-key", cancelled.Token));
    }

    [Fact]
    public async Task The_key_is_sent_upstream_so_the_test_is_real_and_not_a_URL_shape_check()
    {
        // The probe MUST authenticate, or a wrong key would pass — the outcome §3.3 calls out as
        // teaching operators to distrust the button. This asserts the request actually carries it.
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CapsBody, Encoding.UTF8, "application/xml"),
        });
        var prober = new SourceConnectivityProber(new HttpClient(handler));

        await prober.ProbeAsync(BaseUrl, "placeholder-key");

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("apikey=placeholder-key", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("t=caps", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_path_is_preserved_so_a_proxied_source_is_probed_at_the_right_place()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CapsBody, Encoding.UTF8, "application/xml"),
        });
        var prober = new SourceConnectivityProber(new HttpClient(handler));

        await prober.ProbeAsync("http://192.0.2.10:5076/hydra", "placeholder-key");

        Assert.Equal("/hydra/api", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    private static SourceConnectivityProber ProberReturning(HttpResponseMessage response) =>
        new(new HttpClient(new StubHandler(response)));

    private static SourceConnectivityProber ProberThrowing(Exception exception) =>
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage response) => _response = response;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_response);
        }
    }
}
