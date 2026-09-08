using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Arbitarr.Core.Ai;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// #89's connectivity probe, driven through a stub handler so every outcome is produced by the
/// condition it actually names rather than by a fake shortcut.
///
/// <para><b>The property this file exists to pin: the outcomes are DISTINCT.</b> A probe reporting
/// one red "failed" makes the test button decorative, so
/// <see cref="Every_outcome_is_reachable_and_they_are_all_distinct"/> drives all four conditions
/// through the real prober and asserts the results are four different values. That assertion is
/// what fails if a future edit collapses two branches — a size assertion, not a per-case one, so
/// merging TLS into Unreachable cannot pass by leaving each individual case still "correct".</para>
///
/// <para>All addresses are RFC 2606/5737 documentation forms; nothing here can reach a real host,
/// and the stub handler answers without a socket regardless.</para>
/// </summary>
public sealed class OllamaConnectivityProberTests
{
    private const string BaseUrl = "http://ollama.example.com:11434";

    /// <summary>The body a healthy Ollama returns from <c>GET /api/tags</c>.</summary>
    private const string TagsBody = """{"models":[{"name":"qwen2.5:7b-instruct-q4_K_M","size":4700000000}]}""";

    [Fact]
    public async Task A_model_list_response_is_reported_as_ok()
    {
        var outcome = await ProbeAsync(_ => Json(HttpStatusCode.OK, TagsBody));

        Assert.Equal(OllamaProbeOutcome.Ok, outcome);
    }

    /// <summary>
    /// An instance with nothing pulled yet answers <c>{"models":[]}</c> and is perfectly healthy.
    /// Requiring a non-empty array would report a reachable Ollama as "not Ollama" and send the
    /// operator to fix an address that was already right.
    /// </summary>
    [Fact]
    public async Task An_empty_model_list_is_still_a_healthy_ollama()
    {
        var outcome = await ProbeAsync(_ => Json(HttpStatusCode.OK, """{"models":[]}"""));

        Assert.Equal(OllamaProbeOutcome.Ok, outcome);
    }

    [Fact]
    public async Task The_probe_asks_for_the_tags_endpoint()
    {
        Uri? requested = null;
        await ProbeAsync(request =>
        {
            requested = request.RequestUri;
            return Json(HttpStatusCode.OK, TagsBody);
        });

        Assert.NotNull(requested);
        Assert.Equal("/api/tags", requested!.AbsolutePath);
        // No query string: unlike the source probe there is no key to attach, and this is what the
        // Program.cs registration comment relies on when it says the logged URI carries no secret.
        Assert.Equal(string.Empty, requested.Query);
    }

    /// <summary>
    /// An instance behind a reverse proxy at <c>/ollama</c> must be probed at
    /// <c>/ollama/api/tags</c>. Building the URI with a root-anchored relative part would silently
    /// discard the prefix and probe the proxy's own root instead.
    /// </summary>
    [Fact]
    public async Task A_base_path_is_preserved_rather_than_replaced()
    {
        Uri? requested = null;
        await ProbeAsync(
            request =>
            {
                requested = request.RequestUri;
                return Json(HttpStatusCode.OK, TagsBody);
            },
            baseUrl: "http://proxy.example.com/ollama");

        Assert.NotNull(requested);
        Assert.Equal("/ollama/api/tags", requested!.AbsolutePath);
    }

    [Fact]
    public async Task A_socket_failure_is_reported_as_unreachable()
    {
        var outcome = await ProbeAsync(_ => throw new HttpRequestException(
            "connection refused", new SocketException((int)SocketError.ConnectionRefused)));

        Assert.Equal(OllamaProbeOutcome.Unreachable, outcome);
    }

    [Fact]
    public async Task A_tls_handshake_failure_is_reported_as_a_tls_failure()
    {
        var outcome = await ProbeAsync(_ => throw new HttpRequestException(
            "handshake failed", new AuthenticationException("certificate not trusted")));

        Assert.Equal(OllamaProbeOutcome.TlsFailure, outcome);
    }

    [Fact]
    public async Task A_timeout_is_reported_as_unreachable()
    {
        // The prober imposes its own short timeout through a linked token. A handler that never
        // answers must therefore resolve to Unreachable rather than hanging the caller. The stub
        // honours the token the prober passes it, which is what a real handler does.
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, TagsBody);
        });
        using var client = new HttpClient(handler);
        var prober = new OllamaConnectivityProber(client, TimeSpan.FromMilliseconds(50));

        var outcome = await prober.ProbeAsync(BaseUrl);

        Assert.Equal(OllamaProbeOutcome.Unreachable, outcome);
    }

    [Fact]
    public async Task An_html_page_is_reported_as_an_unexpected_response()
    {
        var outcome = await ProbeAsync(_ => Json(HttpStatusCode.OK, "<html><body>Sign in</body></html>"));

        Assert.Equal(OllamaProbeOutcome.UnexpectedResponse, outcome);
    }

    /// <summary>
    /// JSON that parses but is some other service's payload. This is the case a bare "did it answer
    /// 200" check would pass, and it is the most likely real misconfiguration: pointing the setting
    /// at another API on the same host.
    /// </summary>
    [Fact]
    public async Task Another_services_json_is_reported_as_an_unexpected_response()
    {
        var outcome = await ProbeAsync(_ => Json(HttpStatusCode.OK, """{"status":"ok","version":"1.2.3"}"""));

        Assert.Equal(OllamaProbeOutcome.UnexpectedResponse, outcome);
    }

    /// <summary>
    /// A <c>models</c> property of the wrong KIND must not pass. Checking only for the property's
    /// presence would accept <c>{"models":"none"}</c> from an unrelated service.
    /// </summary>
    [Fact]
    public async Task A_models_property_that_is_not_an_array_is_an_unexpected_response()
    {
        var outcome = await ProbeAsync(_ => Json(HttpStatusCode.OK, """{"models":"none"}"""));

        Assert.Equal(OllamaProbeOutcome.UnexpectedResponse, outcome);
    }

    /// <summary>
    /// Ollama has NO authentication, so a service demanding credentials at this address is not
    /// Ollama. Reporting an authentication outcome would send the operator hunting for a key that
    /// does not exist — which is why the enum has four members and not five.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_non_success_status_is_reported_as_an_unexpected_response(HttpStatusCode status)
    {
        var outcome = await ProbeAsync(_ => Json(status, "nope"));

        Assert.Equal(OllamaProbeOutcome.UnexpectedResponse, outcome);
    }

    /// <summary>
    /// A stored value that is not a usable http(s) address cannot be probed at all, and must not
    /// throw out of the endpoint. The write path rejects such a value, so reaching this means an
    /// older row.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://ollama.example.com")]
    public async Task An_unusable_stored_address_is_reported_as_unreachable(string baseUrl)
    {
        var outcome = await ProbeAsync(
            _ => throw new InvalidOperationException("the handler must never be reached"),
            baseUrl: baseUrl);

        Assert.Equal(OllamaProbeOutcome.Unreachable, outcome);
    }

    /// <summary>
    /// Cancellation the CALLER requested is a statement about the caller, not a verdict about the
    /// backend, so it is rethrown rather than classified as Unreachable. Misreporting it would put
    /// "check the address" in front of an operator whose address was fine.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_rethrown_rather_than_classified()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(HttpStatusCode.OK, TagsBody);
        });
        using var client = new HttpClient(handler);
        var prober = new OllamaConnectivityProber(client, TimeSpan.FromSeconds(30));

        using var caller = new CancellationTokenSource();
        var probing = prober.ProbeAsync(BaseUrl, caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probing);
    }

    /// <summary>
    /// <b>THE DISTINCTNESS ASSERTION.</b> Every outcome is produced by driving the real prober
    /// through the condition that causes it, and the four results are asserted to be four different
    /// values by COUNT. A per-case assertion would still pass if a future edit collapsed TLS into
    /// Unreachable and both cases were updated to match; counting the distinct results is what makes
    /// the collapse itself the failure.
    /// </summary>
    [Fact]
    public async Task Every_outcome_is_reachable_and_they_are_all_distinct()
    {
        var outcomes = new List<OllamaProbeOutcome>
        {
            await ProbeAsync(_ => Json(HttpStatusCode.OK, TagsBody)),
            await ProbeAsync(_ => throw new HttpRequestException(
                "refused", new SocketException((int)SocketError.ConnectionRefused))),
            await ProbeAsync(_ => throw new HttpRequestException(
                "handshake", new AuthenticationException("bad certificate"))),
            await ProbeAsync(_ => Json(HttpStatusCode.OK, "<html>login</html>")),
        };

        Assert.Equal(outcomes.Count, outcomes.Distinct().Count());
        // And every member of the enum is covered, so an outcome cannot be added without a case
        // here proving it is actually producible.
        Assert.Equal(
            Enum.GetValues<OllamaProbeOutcome>().OrderBy(o => o).ToList(),
            outcomes.OrderBy(o => o).ToList());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static async Task<OllamaProbeOutcome> ProbeAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string baseUrl = BaseUrl,
        TimeSpan? timeout = null)
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(respond(request)));
        using var client = new HttpClient(handler);
        var prober = new OllamaConnectivityProber(client, timeout ?? TimeSpan.FromSeconds(5));

        return await prober.ProbeAsync(baseUrl);
    }

    /// <summary>
    /// Answers from a delegate instead of a socket. The delegate receives the CancellationToken the
    /// prober passed down, so a stub can model "never answers" honestly — the timeout tests depend
    /// on that token actually reaching the wait.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _respond(request, cancellationToken);
        }
    }
}
