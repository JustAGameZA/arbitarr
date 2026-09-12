using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Arbitarr.Core.Media;
using Arbitarr.Core.Sources;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-6l9b.1: the Radarr connectivity probe must perform a REAL authenticated request and must
/// distinguish its four failure modes, because unreachable / TLS / auth / unexpected-shape have
/// entirely different fixes and a single red "failed" makes the button decorative.
///
/// <para><b>THE KEY-PLACEMENT TEST IS THE LOAD-BEARING ONE HERE.</b> The decision not to call
/// <c>.RemoveAllLoggers()</c> on this client's registration rests on the key riding in the QUERY
/// STRING, where <c>IHttpClientFactory</c>'s logging handler collapses it to <c>?*</c> and
/// <c>LogMessageCleanser</c> scrubs — neither covers a URL PATH segment (CLAUDE.md §1). That is a
/// claim about <c>RadarrConnectivityProber.BuildStatusUri</c>, so it is asserted against the URI the
/// prober really issues rather than left to the registration's comment.</para>
///
/// <para>Addresses are documentation forms (<c>radarr.example</c>, RFC 5737 literals) and every
/// credential-shaped string carries the <c>placeholder-</c> prefix.</para>
/// </summary>
public class RadarrConnectivityProberTests
{
    private const string BaseUrl = "http://radarr.example:7878";
    private const string StatusBody = """{"appName":"Radarr","version":"5.0.0.0"}""";
    private const string ApiKey = "placeholder-radarr-key-1f83bc70";

    [Fact]
    public async Task A_status_document_with_an_accepted_key_is_Ok()
    {
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(StatusBody, Encoding.UTF8, "application/json"),
        });

        Assert.Equal(SourceProbeOutcome.Ok, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    [Fact]
    public async Task A_socket_failure_is_Unreachable()
    {
        // DNS failure / connection refused / no route all surface as a SocketException wrapped in an
        // HttpRequestException — the "check the address" case.
        var prober = ProberThrowing(new HttpRequestException(
            "connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(SourceProbeOutcome.Unreachable, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    [Fact]
    public async Task A_TLS_handshake_failure_is_TlsFailure_and_not_merely_Unreachable()
    {
        // The distinction that matters most: the address is RIGHT and the transport is the problem,
        // so telling the operator "unreachable" would send them to check the wrong thing.
        var prober = ProberThrowing(new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid.")));

        Assert.Equal(SourceProbeOutcome.TlsFailure, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_is_AuthenticationFailed(HttpStatusCode status)
    {
        // The headline case, and the reason the probe cannot be a URL-shape check: Radarr is
        // reachable and healthy, and only the key is wrong.
        var prober = ProberReturning(new HttpResponseMessage(status));

        Assert.Equal(
            SourceProbeOutcome.AuthenticationFailed,
            await prober.ProbeAsync(BaseUrl, "placeholder-radarr-key-wrong"));
    }

    [Fact]
    public async Task A_login_page_instead_of_the_API_is_UnexpectedResponse()
    {
        // 200 OK carrying HTML: the classic "base URL points at a reverse proxy or the wrong service"
        // case. Reporting Ok here is exactly the false pass the probe exists to prevent.
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Please sign in</body></html>", Encoding.UTF8, "text/html"),
        });

        Assert.Equal(SourceProbeOutcome.UnexpectedResponse, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    [Fact]
    public async Task A_server_error_is_UnexpectedResponse_rather_than_Unreachable()
    {
        // A 500 means something answered: the address is right, so "unreachable" would mislead.
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Equal(SourceProbeOutcome.UnexpectedResponse, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    /// <summary>
    /// The status document is recognised on its SHAPE rather than on an exact <c>appName</c> value, so
    /// a Radarr version that renames a field does not read as "not Radarr". Pinned because the
    /// obvious "improvement" — matching <c>appName == "Radarr"</c> — would break against any fork or
    /// proxy answering a compatible document, and the prober's doc records that as a decision.
    /// </summary>
    [Theory]
    [InlineData("""{"appName":"Radarr","version":"5.0.0.0"}""")]
    [InlineData("""{"version":"5.0.0.0"}""")]
    [InlineData("""{"appName":"Radarr"}""")]
    public async Task The_status_document_is_recognised_on_its_shape_rather_than_an_exact_value(string body)
    {
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        Assert.Equal(SourceProbeOutcome.Ok, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""["an","array"]""")]
    [InlineData("""{"unrelated":"document"}""")]
    public async Task A_body_that_is_not_a_status_document_is_UnexpectedResponse(string body)
    {
        var prober = ProberReturning(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        Assert.Equal(SourceProbeOutcome.UnexpectedResponse, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    [Fact]
    public async Task The_probe_times_out_rather_than_hanging_the_UI()
    {
        // A wrong host should fail in seconds, not hang the UI. A handler that never completes must
        // still produce a verdict, via the prober's own short timeout.
        var prober = new RadarrConnectivityProber(
            new HttpClient(new NeverRespondingHandler()),
            timeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(SourceProbeOutcome.Unreachable, await prober.ProbeAsync(BaseUrl, ApiKey));
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_rather_than_reported_as_a_radarr_failure()
    {
        // An aborted request (operator navigated away, host shutting down) says nothing about Radarr,
        // so it must not be recorded as a verdict against it.
        var prober = new RadarrConnectivityProber(new HttpClient(new NeverRespondingHandler()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prober.ProbeAsync(BaseUrl, ApiKey, cancelled.Token));
    }

    /// <summary>
    /// THE KEY IS SENT UPSTREAM, AND IN THE QUERY STRING.
    ///
    /// <para>The first half is why the probe is real rather than a URL-shape check: without the key a
    /// wrong one would pass. The second half is what the "no <c>.RemoveAllLoggers()</c>" decision on
    /// this client's registration rests on — the query string is where the logging handler's <c>?*</c>
    /// collapse fires and where <c>LogMessageCleanser</c> scrubs, and neither covers a PATH segment.
    /// Both are asserted against the URI the prober actually issued.</para>
    /// </summary>
    [Fact]
    public async Task The_key_is_sent_in_the_query_string_and_never_in_the_path()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(StatusBody, Encoding.UTF8, "application/json"),
        });
        var prober = new RadarrConnectivityProber(new HttpClient(handler));

        await prober.ProbeAsync(BaseUrl, ApiKey);

        Assert.NotNull(handler.LastRequest);
        var uri = handler.LastRequest!.RequestUri!;

        // It authenticates at all...
        Assert.Contains($"apikey={ApiKey}", uri.Query, StringComparison.Ordinal);
        // ...against the endpoint Radarr answers only for an accepted key...
        Assert.Equal("/api/v3/system/status", uri.AbsolutePath);
        // ...and the key is NOT in the path, which is the placement neither log layer covers.
        Assert.DoesNotContain(ApiKey, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);

        // Nor in a header: a second placement would split the codebase's one convention and
        // invalidate the registration comment that SonarrKeyIsScrubbedFromLogsTests and
        // DisableUriRedactionSwitchTests pin. Asserted rather than assumed.
        Assert.False(handler.LastRequest.Headers.Contains("X-Api-Key"));
    }

    /// <summary>
    /// A base path is preserved, so a Radarr behind a reverse proxy at a sub-path is probed at the
    /// right place rather than at the proxy's root.
    /// </summary>
    [Fact]
    public async Task A_base_path_is_preserved_so_a_proxied_instance_is_probed_at_the_right_place()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(StatusBody, Encoding.UTF8, "application/json"),
        });
        var prober = new RadarrConnectivityProber(new HttpClient(handler));

        await prober.ProbeAsync("http://radarr.example:7878/radarr/", ApiKey);

        Assert.Equal("/radarr/api/v3/system/status", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    private static RadarrConnectivityProber ProberReturning(HttpResponseMessage response) =>
        new(new HttpClient(new StubHandler(response)));

    private static RadarrConnectivityProber ProberThrowing(Exception exception) =>
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
