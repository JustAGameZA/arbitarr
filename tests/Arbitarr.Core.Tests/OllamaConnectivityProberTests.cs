using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
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
/// <para><b>#112 added a second property: the MODEL NAMES come back, and the outcome still does
/// not.</b> Those tests live in their own block at the bottom. Two of them restate guarantees the
/// tests above already hold — that an empty array is healthy, and that no text from the wire reaches
/// the outcome — from the result's side, because extracting names is exactly the change that could
/// have broken either while every assertion above still passed.</para>
///
/// <para>All addresses are RFC 2606/5737 documentation forms; nothing here can reach a real host,
/// and the stub handler answers without a socket regardless. Model names are either the real
/// public tag names (which are not secrets) or <c>placeholder-*</c>.</para>
/// </summary>
public sealed class OllamaConnectivityProberTests
{
    private const string BaseUrl = "http://ollama.example.com:11434";

    /// <summary>The body a healthy Ollama returns from <c>GET /api/tags</c>.</summary>
    private const string TagsBody = """{"models":[{"name":"qwen2.5:7b-instruct-q4_K_M","size":4700000000}]}""";

    /// <summary>
    /// arb-1rr: the model the helpers send the <c>/api/chat</c> half of the probe with. A public tag
    /// name, not a secret.
    /// </summary>
    private const string ProbeModel = "qwen2.5:7b-instruct-q4_K_M";

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
        // arb-1rr: the FIRST request. A successful probe now also posts to /api/chat, and this
        // assertion is about the tags request specifically.
        Uri? requested = null;
        await ProbeAsync(request =>
        {
            requested ??= request.RequestUri;
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
        // arb-1rr: the FIRST request, not the last — a successful probe now makes a second one to
        // /api/chat, and capturing unconditionally would assert against that instead.
        Uri? requested = null;
        await ProbeAsync(
            request =>
            {
                requested ??= request.RequestUri;
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

        var result = await prober.ProbeAsync(BaseUrl);

        Assert.Equal(OllamaProbeOutcome.Unreachable, result.Outcome);
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
        var probing = prober.ProbeAsync(BaseUrl, cancellationToken: caller.Token);
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

            // arb-1rr's two. Tags succeeds in both; what differs is whether there was a model to
            // post and whether Ollama accepted it.
            await ProbeAsync(RespondPerEndpoint(
                tags: Json(HttpStatusCode.OK, TagsBody),
                chat: () => Json(HttpStatusCode.BadRequest, ChatErrorBody))),
            await ProbeAsync(_ => Json(HttpStatusCode.OK, TagsBody), model: null),
        };

        Assert.Equal(outcomes.Count, outcomes.Distinct().Count());
        // And every member of the enum is covered, so an outcome cannot be added without a case
        // here proving it is actually producible.
        Assert.Equal(
            Enum.GetValues<OllamaProbeOutcome>().OrderBy(o => o).ToList(),
            outcomes.OrderBy(o => o).ToList());
    }

    // ---------------------------------------------------------------------------------------
    // #112: the model NAMES the probe now carries back. Every assertion below is about the
    // Models field, never about the outcome wording — that is still derived from the enum alone,
    // and the tests above are what pin it.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The names are extracted from the <c>name</c> of each <c>/api/tags</c> entry, IN ORDER.
    /// Order matters because it is what the operator sees in the picker, and Ollama lists most
    /// recently modified first — resorting it here would silently discard that.
    /// </summary>
    [Fact]
    public async Task The_model_names_are_extracted_from_the_tags_response()
    {
        var result = await ProbeForResultAsync(_ => Json(HttpStatusCode.OK, """
            {"models":[
              {"name":"qwen2.5:7b-instruct-q4_K_M","size":4700000000},
              {"name":"llama3.1:8b","size":4900000000},
              {"name":"phi4:14b","size":9100000000}
            ]}
            """));

        Assert.Equal(OllamaProbeOutcome.Ok, result.Outcome);
        Assert.Equal(
            new[] { "qwen2.5:7b-instruct-q4_K_M", "llama3.1:8b", "phi4:14b" },
            result.Models);
    }

    /// <summary>
    /// <b>THE EMPTY-ARRAY GUARANTEE, restated for #112.</b> An instance with nothing pulled is
    /// healthy, so it is still <see cref="OllamaProbeOutcome.Ok"/> — with an empty name list rather
    /// than a failure. The obvious way to implement name extraction is to treat "no names" as "not
    /// a model list", and that would report a perfectly reachable Ollama as "not Ollama" and send
    /// the operator to fix an address that was already right. Asserted on the RESULT here, where
    /// <see cref="An_empty_model_list_is_still_a_healthy_ollama"/> above asserts the outcome, so
    /// the two halves of the guarantee each have their own failure.
    /// </summary>
    [Fact]
    public async Task An_empty_model_list_is_ok_with_no_names_rather_than_a_failure()
    {
        var result = await ProbeForResultAsync(_ => Json(HttpStatusCode.OK, """{"models":[]}"""));

        Assert.Equal(OllamaProbeOutcome.Ok, result.Outcome);
        Assert.Empty(result.Models);
    }

    /// <summary>
    /// An entry with no usable <c>name</c> is SKIPPED, not fatal. A body that is recognisably
    /// Ollama's model list stays recognisable when one element is odd, and a name the picker could
    /// not render is simply not offered. Per-entry: the two good names on either side of the bad
    /// ones must both survive, which a "stop at the first bad entry" implementation would fail.
    /// </summary>
    [Fact]
    public async Task An_entry_without_a_usable_name_is_skipped_rather_than_failing_the_probe()
    {
        var result = await ProbeForResultAsync(_ => Json(HttpStatusCode.OK, """
            {"models":[
              {"name":"llama3.1:8b"},
              {"size":123},
              {"name":null},
              {"name":42},
              {"name":"  "},
              {"name":""},
              "not-an-object",
              {"name":"phi4:14b"}
            ]}
            """));

        Assert.Equal(OllamaProbeOutcome.Ok, result.Outcome);
        Assert.Equal(new[] { "llama3.1:8b", "phi4:14b" }, result.Models);
    }

    /// <summary>
    /// Every non-Ok outcome carries an EMPTY list. There was no model list to read, so anything
    /// there would have to have been invented — and a picker populated from a failed probe is
    /// exactly the "green tick, wrong model" failure #112 exists to end.
    /// </summary>
    [Fact]
    public async Task Every_failing_outcome_carries_no_model_names()
    {
        var failures = new List<OllamaProbeResult>
        {
            await ProbeForResultAsync(_ => throw new HttpRequestException(
                "refused", new SocketException((int)SocketError.ConnectionRefused))),
            await ProbeForResultAsync(_ => throw new HttpRequestException(
                "handshake", new AuthenticationException("bad certificate"))),
            await ProbeForResultAsync(_ => Json(HttpStatusCode.OK, "<html>login</html>")),
            await ProbeForResultAsync(_ => Json(HttpStatusCode.Unauthorized, "nope")),
            await ProbeForResultAsync(
                _ => throw new InvalidOperationException("unreachable"), baseUrl: "not-a-url"),
        };

        // Per-result, not "none of them collectively": a single Assert.All over a flattened list
        // would still pass if one result carried names and another carried the empty list.
        Assert.All(failures, f =>
        {
            Assert.NotEqual(OllamaProbeOutcome.Ok, f.Outcome);
            Assert.Empty(f.Models);
        });
        // And the set really did cover more than one failing outcome, so the assertion above is not
        // one case repeated five times.
        Assert.True(failures.Select(f => f.Outcome).Distinct().Count() >= 3);
    }

    /// <summary>
    /// <b>THE CLOSED-ENUM GUARANTEE, restated for #112.</b> Carrying model names must not have
    /// leaked one into the outcome, which is the only thing the endpoint's operator-facing wording
    /// is derived from. The outcome remains a bare enum value with no room for text — asserted by
    /// its being a member of the enum whose name matches none of the models reported.
    /// </summary>
    [Fact]
    public async Task The_outcome_carries_no_model_name()
    {
        const string distinctive = "placeholder-model-name-that-must-not-escape";
        var result = await ProbeForResultAsync(_ => Json(
            HttpStatusCode.OK, $$"""{"models":[{"name":"{{distinctive}}"}]}"""));

        // Existence: the name really was in the response and really was read.
        Assert.Contains(distinctive, result.Models);

        // Detectability: the search below genuinely finds this string when it is present, so its
        // absence from the outcome is a property rather than a search that cannot see.
        Assert.Contains(distinctive, $"Ok: {distinctive}", StringComparison.Ordinal);
        Assert.DoesNotContain(distinctive, result.Outcome.ToString(), StringComparison.Ordinal);
        Assert.Contains(result.Outcome, Enum.GetValues<OllamaProbeOutcome>());
    }

    // ---------------------------------------------------------------------------------------
    // arb-1rr: the /api/chat half. The tags probe confirms the ADDRESS; these pin that the probe
    // also confirms the thing at that address will actually classify — the gap that let the button
    // report "Connected successfully" while every real call failed 400.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The chat probe runs only AFTER tags succeeds, and it is the real <c>/api/chat</c> endpoint
    /// that is posted to — not a second tags call.
    /// </summary>
    [Fact]
    public async Task A_successful_tags_probe_is_followed_by_a_post_to_the_chat_endpoint()
    {
        var seen = new List<(string Method, string Path)>();
        await ProbeForResultAsync(request =>
        {
            seen.Add((request.Method.Method, request.RequestUri!.AbsolutePath));
            return request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, ChatOkBody)
                : Json(HttpStatusCode.OK, TagsBody);
        });

        Assert.Equal(
            new[] { ("GET", "/api/tags"), ("POST", "/api/chat") },
            seen);
    }

    /// <summary>
    /// The chat URI preserves a base path exactly as the tags URI does — an Ollama behind a reverse
    /// proxy at <c>/ollama</c> must be probed at <c>/ollama/api/chat</c>. A <c>new Uri(base,
    /// "/api/chat")</c> would silently root-anchor and drop the prefix, and the probe would report a
    /// rejection from an address it was never asked about.
    /// </summary>
    [Fact]
    public async Task The_chat_request_preserves_a_base_path()
    {
        var paths = new List<string>();
        await ProbeForResultAsync(
            request =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                return request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.OK, ChatOkBody)
                    : Json(HttpStatusCode.OK, TagsBody);
            },
            baseUrl: "http://proxy.example.com/ollama");

        Assert.Equal(new[] { "/ollama/api/tags", "/ollama/api/chat" }, paths);
    }

    /// <summary>
    /// <b>A FAILED TAGS PROBE MUST NOT POST.</b> The address is already known to be wrong, so paying
    /// for a model load against it would be pure cost, and a chat outcome from an address that does
    /// not answer would misdirect the operator. Asserted by COUNTING requests: "the outcome was
    /// Unreachable" would still pass if the chat request had been sent and then ignored.
    /// </summary>
    [Fact]
    public async Task A_failed_tags_probe_sends_no_chat_request()
    {
        var requests = 0;
        var result = await ProbeForResultAsync(_ =>
        {
            requests++;
            return Json(HttpStatusCode.OK, "<html>login</html>");
        });

        Assert.Equal(OllamaProbeOutcome.UnexpectedResponse, result.Outcome);
        Assert.Equal(1, requests);
    }

    /// <summary>
    /// With no model configured there is nothing to post, so the chat half is skipped — and the
    /// result says so rather than claiming an Ok it did not earn. Counted, for the same reason as
    /// above.
    /// </summary>
    [Fact]
    public async Task No_configured_model_reports_the_tags_only_outcome_and_sends_no_chat_request()
    {
        var requests = 0;
        var result = await ProbeForResultAsync(
            _ =>
            {
                requests++;
                return Json(HttpStatusCode.OK, TagsBody);
            },
            model: null);

        Assert.Equal(OllamaProbeOutcome.OkNoModelConfigured, result.Outcome);
        Assert.Equal(1, requests);
        // The model list still comes back: the address WAS confirmed, and the picker is how the
        // operator fixes the missing model.
        Assert.Equal(new[] { "qwen2.5:7b-instruct-q4_K_M" }, result.Models);
    }

    /// <summary>
    /// The request is shaped like a real classification — the selected model, <c>stream:false</c>,
    /// and the shared <see cref="VerdictSchema"/> <c>format</c>. Those are the fields Ollama
    /// validates, so a probe missing any of them could pass where a real call fails.
    /// </summary>
    [Fact]
    public async Task The_chat_request_carries_the_model_stream_false_and_the_verdict_schema()
    {
        string? body = null;
        await ProbeForResultAsync(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.OK, ChatOkBody);
            }

            return Json(HttpStatusCode.OK, TagsBody);
        });

        Assert.NotNull(body);
        using var sent = JsonDocument.Parse(body!);
        var root = sent.RootElement;

        Assert.Equal(ProbeModel, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());

        // The format is the SHARED constant, not a copy: comparing against the parsed schema is what
        // fails if the prober ever grows its own divergent version.
        using var expected = JsonDocument.Parse(VerdictSchema.Object);
        Assert.Equal(
            JsonSerializer.Serialize(expected.RootElement),
            JsonSerializer.Serialize(root.GetProperty("format")));
    }

    /// <summary>
    /// <b>THE BEAD'S CENTRAL CASE.</b> Tags answers healthily and the chat request is rejected 400 —
    /// the exact shape that reported "Connected successfully" before arb-1rr. It must now be its own
    /// outcome, and it must carry the reason Ollama actually gave.
    /// </summary>
    [Fact]
    public async Task A_rejected_chat_request_is_reported_with_the_upstream_reason()
    {
        var result = await ProbeForResultAsync(RespondPerEndpoint(
            tags: Json(HttpStatusCode.OK, TagsBody),
            chat: () => Json(HttpStatusCode.BadRequest, ChatErrorBody)));

        Assert.Equal(OllamaProbeOutcome.ChatRejected, result.Outcome);
        Assert.Contains("400", result.ChatError, StringComparison.Ordinal);
        // The upstream reason itself, which is the whole point: without it the operator learns only
        // "something was rejected".
        Assert.Contains("missing unit in duration", result.ChatError, StringComparison.Ordinal);
        // The model list survives the chat failure — the address was confirmed, and the picker is
        // how the operator changes the model this reason is complaining about.
        Assert.Equal(new[] { "qwen2.5:7b-instruct-q4_K_M" }, result.Models);
    }

    /// <summary>
    /// <b>THE TOPOLOGY GUARANTEE, at the prober.</b> <see cref="OllamaProbeResult.ChatError"/>
    /// reaches an operator-facing response, so a host or a credential echoed in Ollama's error body
    /// must not ride along. Both halves are POSITIVE CONTROLS: each planted value is first shown to
    /// be findable by the very search that then asserts its absence, so neither assertion can pass
    /// vacuously against text that never contained it.
    /// </summary>
    [Fact]
    public async Task A_rejected_chat_request_scrubs_a_planted_host_and_key_from_the_reason()
    {
        const string plantedHost = "ollama.internal.example";
        const string plantedKey = "SUPERSECRETVALUE";
        var planted = $$"""{"error":"upstream {{plantedHost}}:11434 refused apikey={{plantedKey}}"}""";

        // Detectability: these searches genuinely find these strings when they ARE present, so the
        // absence assertions below are properties rather than searches that cannot see.
        Assert.Contains(plantedHost, planted, StringComparison.Ordinal);
        Assert.Contains(plantedKey, planted, StringComparison.Ordinal);

        var result = await ProbeForResultAsync(RespondPerEndpoint(
            tags: Json(HttpStatusCode.OK, TagsBody),
            chat: () => Json(HttpStatusCode.BadRequest, planted)));

        Assert.Equal(OllamaProbeOutcome.ChatRejected, result.Outcome);
        Assert.DoesNotContain(plantedHost, result.ChatError, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedKey, result.ChatError, StringComparison.Ordinal);
        // And the scrubbing really ran, rather than the whole excerpt having been dropped: the
        // redaction token is present, which is what distinguishes "scrubbed" from "never arrived".
        Assert.Contains(
            Arbitarr.Core.Diagnostics.SanitizedErrorDescription.Replacement,
            result.ChatError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every outcome other than <see cref="OllamaProbeOutcome.ChatRejected"/> carries an EMPTY
    /// reason. Asserted per result, because "none of them collectively" would still pass if one
    /// carried text and another did not.
    /// </summary>
    [Fact]
    public async Task Only_a_rejected_chat_request_carries_a_reason()
    {
        var results = new List<OllamaProbeResult>
        {
            await ProbeForResultAsync(_ => Json(HttpStatusCode.OK, TagsBody)),
            await ProbeForResultAsync(_ => Json(HttpStatusCode.OK, TagsBody), model: null),
            await ProbeForResultAsync(_ => Json(HttpStatusCode.OK, "<html>login</html>")),
            await ProbeForResultAsync(_ => throw new HttpRequestException(
                "refused", new SocketException((int)SocketError.ConnectionRefused))),
        };

        Assert.All(results, r =>
        {
            Assert.NotEqual(OllamaProbeOutcome.ChatRejected, r.Outcome);
            Assert.Equal(string.Empty, r.ChatError);
        });
        Assert.True(results.Select(r => r.Outcome).Distinct().Count() >= 3);
    }

    /// <summary>The body a healthy Ollama returns from a <c>POST /api/chat</c>.</summary>
    private const string ChatOkBody =
        """{"message":{"content":"{\"verdict\":\"accept\",\"confidence\":0.9}"}}""";

    /// <summary>
    /// The 400 body that motivated this bead — Ollama's real wording when <c>keep_alive</c> reached
    /// it as a unit-less duration STRING.
    /// </summary>
    private const string ChatErrorBody = """{"error":"time: missing unit in duration \"-1\""}""";

    /// <summary>
    /// Answers <c>/api/chat</c> and <c>/api/tags</c> differently, which is what the two-request
    /// probe needs to model a healthy address with an unhappy model. The chat response is built per
    /// call because an <see cref="HttpResponseMessage"/> cannot be served twice.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> RespondPerEndpoint(
        HttpResponseMessage tags,
        Func<HttpResponseMessage> chat) =>
        request => request.RequestUri!.AbsolutePath.EndsWith("/api/chat", StringComparison.Ordinal)
            ? chat()
            : tags;

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    /// <summary>Drives the prober and returns the OUTCOME alone, for the classification tests.</summary>
    private static async Task<OllamaProbeOutcome> ProbeAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string baseUrl = BaseUrl,
        TimeSpan? timeout = null,
        string? model = ProbeModel) =>
        (await ProbeForResultAsync(respond, baseUrl, timeout, model)).Outcome;

    /// <summary>
    /// Drives the prober and returns the whole result, for the #112 model-name tests.
    ///
    /// <para>arb-1rr: <paramref name="model"/> defaults to <see cref="ProbeModel"/> so the helper
    /// keeps modelling a CONFIGURED instance, which is what every test written before this bead
    /// assumed. The stub answers both requests (the delegate sees a second call for
    /// <c>/api/chat</c>), so a test that asserts <see cref="OllamaProbeOutcome.Ok"/> still means
    /// "both halves passed". Pass null explicitly to model an unconfigured instance.</para>
    /// </summary>
    private static async Task<OllamaProbeResult> ProbeForResultAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string baseUrl = BaseUrl,
        TimeSpan? timeout = null,
        string? model = ProbeModel)
    {
        using var handler = new StubHandler((request, _) => Task.FromResult(respond(request)));
        using var client = new HttpClient(handler);
        var prober = new OllamaConnectivityProber(client, timeout ?? TimeSpan.FromSeconds(5));

        return await prober.ProbeAsync(baseUrl, model);
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
