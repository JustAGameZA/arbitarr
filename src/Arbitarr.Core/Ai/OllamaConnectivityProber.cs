using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arbitarr.Core.Ai;

/// <summary>
/// #89's connectivity test for the AI backend: a REAL request to Ollama's own
/// <c>GET /api/tags</c>, classified into one of the <see cref="OllamaProbeOutcome"/> modes.
///
/// <para><b>Ollama's own API, not a generic reachability check.</b> <c>/api/tags</c> is the model
/// list — the cheapest endpoint Ollama serves that no other service would answer in the same shape,
/// and one that needs no model loaded and starts no inference, so probing costs the backend
/// nothing. A bare TCP connect or a <c>GET /</c> would pass against anything listening on the port,
/// which is precisely the mistake <see cref="OllamaProbeOutcome.UnexpectedResponse"/> exists to
/// report: pointing this setting at the wrong service is the most likely way to misconfigure it,
/// and a probe that cannot detect that teaches the operator to distrust the button.</para>
///
/// <para><b>And then <c>/api/chat</c> — arb-1rr.</b> This class originally probed <c>/api/tags</c>
/// ALONE, reasoning that classification's own endpoint loads the model (up to a ~59s cold start,
/// docs/step0-measurements.md) and that a 404 for an unpulled model is a model problem rather than a
/// connectivity one. That reasoning was sound about cost and wrong about what the button promises.
/// A <c>keep_alive</c> serialisation bug had <c>/api/tags</c> answering perfectly while EVERY
/// classification failed 400, so the operator was told "Connected successfully" by the only
/// affordance offered for checking — and the class of fault survives that specific bug: any 400 from
/// <c>/api/chat</c> (a rejected option, an unknown model, a schema refused) is invisible to a probe
/// that never posts one.</para>
///
/// <para>So a successful tags probe is now FOLLOWED by one minimal <c>/api/chat</c> request built
/// the way <c>OllamaClient</c> builds a real one — same <see cref="VerdictSchema"/> <c>format</c>
/// (shared, not copied, which is why that constant lives in Core), <c>stream:false</c>, the selected
/// model. The model-load cost is not an objection here the way it was for an automatic check: this
/// probe runs only when an operator presses Test, and loading the model is precisely the thing they
/// are asking to have verified. It runs AFTER tags rather than instead of it so the two failures
/// stay distinguishable — a wrong address still reports as a wrong address, and
/// <see cref="OllamaProbeOutcome.ChatRejected"/> means the address was right and the request was
/// not.</para>
///
/// <para>With NO model configured there is nothing to post, so the chat half is skipped and the
/// result says so (<see cref="OllamaProbeOutcome.OkNoModelConfigured"/>) rather than claiming a
/// success it did not test.</para>
///
/// <para><b>Short timeout.</b> <see cref="DefaultTimeout"/> is imposed here through a linked
/// cancellation token rather than via <see cref="HttpClient.Timeout"/> — the caller's client is
/// shared and its timeout is not this type's to mutate. Shorter than the source prober's 10s
/// because Ollama is local infrastructure rather than an internet indexer: a healthy instance on
/// the same host or LAN answers <c>/api/tags</c> in milliseconds, so a wrong address fails while
/// the operator is still looking at the button.</para>
///
/// <para><b>Nothing from the wire reaches the OPERATOR-FACING WORDING.</b> The outcome is a closed
/// enum with no string member, so no branch can put the upstream body, an exception message, or the
/// probed URL into the sentence the endpoint composes. Cancellation the CALLER requested is rethrown
/// rather than classified, so an aborted request is never misreported as a backend failure.
///
/// <para>arb-1rr qualifies this in one place and no more: <see cref="OllamaProbeResult.ChatError"/>
/// carries upstream text, BESIDE the enum in its own field, exactly as <c>Models</c> already did.
/// The sentence is still composed from the enum alone. That text is passed through
/// <see cref="Arbitarr.Core.Diagnostics.SanitizedErrorDescription"/> before it is stored, so it
/// holds no host, address or credential — this method never assigns a raw response body to
/// it.</para></para>
///
/// <para><b>#112: the model NAMES do come back, in their own field.</b> <c>/api/tags</c> is the
/// model list and the probe was already reading it to classify — discarding its contents is what
/// left an operator able to see "Connected successfully" against an instance that had never pulled
/// the configured model. <see cref="OllamaProbeResult.Models"/> carries the names beside the enum,
/// never inside it: they are rendered as list items to choose from, and the wording is still derived
/// from the enum alone. A malformed entry is SKIPPED rather than making the probe fail — the
/// question the button answers is "is this Ollama", and one odd entry is not a no.</para>
/// </summary>
public sealed class OllamaConnectivityProber
{
    /// <summary>
    /// The probe's timeout: 5 seconds. Long enough for a busy local instance to answer a model-list
    /// request, short enough that a wrong address fails while the operator is still watching. Equal
    /// to <c>OllamaOptions.CallTimeout</c> by coincidence of purpose rather than by sharing — that
    /// constant lives in Arbitarr.Ai, which Core does not reference (AC6).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public OllamaConnectivityProber(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Probes <paramref name="baseUrl"/> and classifies the result. Never throws for a backend-side
    /// failure — every reachable-world outcome is an <see cref="OllamaProbeOutcome"/> — and never
    /// surfaces the response body or the address. On <see cref="OllamaProbeOutcome.Ok"/> the
    /// result also carries the model names the instance reported (#112).
    /// </summary>
    /// <param name="baseUrl">The address to probe.</param>
    /// <param name="model">
    /// arb-1rr: the model to send the <c>/api/chat</c> half of the probe with. When null, empty or
    /// whitespace the chat probe is SKIPPED and a successful tags probe reports
    /// <see cref="OllamaProbeOutcome.OkNoModelConfigured"/> — there is nothing to post, and claiming
    /// <see cref="OllamaProbeOutcome.Ok"/> would assert a classification path that was not tested.
    /// Defaulted so the existing single-argument call shape keeps its old tags-only meaning.
    /// </param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public async Task<OllamaProbeResult> ProbeAsync(
        string baseUrl,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            // A stored value that is not a usable address cannot be probed at all. Reported as
            // unreachable rather than thrown: from the operator's chair "this address goes nowhere"
            // is the same next step, and the settings write path already rejects such a value, so
            // reaching here at all means the row predates that validation.
            return OllamaProbeResult.From(OllamaProbeOutcome.Unreachable);
        }

        var probeUri = BuildTagsUri(parsed);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var response = await _httpClient
                .GetAsync(probeUri, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Includes 401/403. Ollama has no authentication, so a service demanding
                // credentials at this address is not Ollama — an authentication outcome would
                // send the operator hunting for a key that does not exist. See
                // OllamaProbeOutcome's note on why there are four outcomes and not five.
                return OllamaProbeResult.From(OllamaProbeOutcome.UnexpectedResponse);
            }

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            if (!TryReadTagsResponse(body, out var models))
            {
                return OllamaProbeResult.From(OllamaProbeOutcome.UnexpectedResponse);
            }

            // arb-1rr: the tags probe has confirmed the ADDRESS. Whether the thing the address
            // points at will actually classify is a separate question, and the one an operator is
            // really asking. Only reached on a tags success, so a wrong address never pays the
            // model-load cost and never reports a chat outcome.
            if (string.IsNullOrWhiteSpace(model))
            {
                return new OllamaProbeResult(OllamaProbeOutcome.OkNoModelConfigured, models);
            }

            return await ProbeChatAsync(parsed, model, models, timeoutSource.Token, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away (request aborted, host shutting down). That is not a verdict
            // about the backend, so it must not be reported as one.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout fired: the backend never answered in time.
            return OllamaProbeResult.From(OllamaProbeOutcome.Unreachable);
        }
        catch (HttpRequestException ex)
        {
            return OllamaProbeResult.From(Classify(ex));
        }
    }

    /// <summary>
    /// arb-1rr: posts ONE minimal classification-shaped request to <c>/api/chat</c> and reports
    /// whether Ollama accepted it.
    ///
    /// <para><b>The request must be shaped like a real classification, or it proves nothing.</b>
    /// Same <see cref="VerdictSchema"/> <c>format</c> (the shared constant, so the two cannot
    /// drift), <c>stream:false</c>, the selected model — those are the fields Ollama validates and
    /// rejects. The prompt itself is a single throwaway user message rather than the real
    /// <c>ClassificationPrompt</c>: its CONTENT is the one part Ollama does not validate, and
    /// building it here would drag the whole prompt layer into Core for no added signal.</para>
    ///
    /// <para>No <c>keep_alive</c> is sent, and the request record is local to this class — see
    /// <see cref="ProbeChatRequest"/> for both reasons.</para>
    ///
    /// <para>A non-2xx is <see cref="OllamaProbeOutcome.ChatRejected"/> carrying the SCRUBBED
    /// reason. A transport failure at this stage is classified exactly as one against
    /// <c>/api/tags</c> would be: the address answered a moment ago, so a connection that now fails
    /// is a real transport fault and reporting it as a rejected request would misdirect the
    /// operator.</para>
    /// </summary>
    private async Task<OllamaProbeResult> ProbeChatAsync(
        Uri baseUri,
        string model,
        IReadOnlyList<string> models,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        var chatUri = BuildChatUri(baseUri);

        var payload = new ProbeChatRequest(
            model,
            [new ProbeChatMessage("user", "ping")],
            Stream: false,
            Format: JsonSerializer.Deserialize<JsonElement>(VerdictSchema.Object));

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await _httpClient
                .PostAsync(chatUri, content, timeoutToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new OllamaProbeResult(OllamaProbeOutcome.Ok, models);
            }

            // The body is read and scrubbed through the SAME path the classifier's failures take,
            // so what an operator sees here and what lands on the dashboard cannot disagree.
            var failure = await OllamaRequestException
                .FromResponseAsync(response, timeoutToken)
                .ConfigureAwait(false);

            return new OllamaProbeResult(
                OllamaProbeOutcome.ChatRejected,
                models,
                Diagnostics.SanitizedErrorDescription.Describe(failure));
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout. A cold model load can legitimately outrun the probe's short budget,
            // so this is reported as unreachable (the honest "no answer in time") rather than as a
            // rejection — nothing was rejected, and telling an operator their model is broken
            // because it was slow to load would be worse than telling them nothing.
            return OllamaProbeResult.From(OllamaProbeOutcome.Unreachable);
        }
        catch (HttpRequestException ex)
        {
            return OllamaProbeResult.From(Classify(ex));
        }
    }

    /// <summary>
    /// Separates a TLS failure from an ordinary connection failure, exactly as
    /// <c>SourceConnectivityProber.Classify</c> does and for the same reasons: both arrive as
    /// <see cref="HttpRequestException"/>, so the distinction lives in the inner exception, and the
    /// message text is never inspected (localised, platform-specific) nor propagated.
    /// </summary>
    private static OllamaProbeOutcome Classify(HttpRequestException exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return OllamaProbeOutcome.TlsFailure;
            }

            if (inner is SocketException)
            {
                return OllamaProbeOutcome.Unreachable;
            }
        }

        return exception.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => OllamaProbeOutcome.TlsFailure,
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => OllamaProbeOutcome.Unreachable,
            // A transport failure we cannot attribute more precisely is reported as unreachable
            // rather than as an unexpected response: no usable answer was received, so "check the
            // address" is the honest next step to suggest.
            _ => OllamaProbeOutcome.Unreachable,
        };
    }

    /// <summary>
    /// Whether the body is the model list Ollama actually returns, rather than some other service's
    /// 200 — and, when it is, the <c>name</c> of each entry (#112).
    ///
    /// <para><b>AN EMPTY ARRAY IS STILL A YES.</b> <c>/api/tags</c> answers <c>{"models":[...]}</c>,
    /// and an EMPTY array is a perfectly healthy instance with nothing pulled yet — so the check is
    /// for the <c>models</c> property being present and an array, never for it being non-empty, and
    /// never for the extracted name list being non-empty either. Requiring a model here would report
    /// a reachable Ollama as "not Ollama" and send the operator to fix an address that was already
    /// correct. #112 added the extraction below and must not have quietly added that requirement
    /// with it: the return value is decided before a single name is read.</para>
    ///
    /// <para>An entry without a usable string <c>name</c> is SKIPPED rather than rejecting the whole
    /// response, for the same reason: a body that is recognisably Ollama's model list stays
    /// recognisable when one element is odd, and a name the picker cannot render is simply not
    /// offered.</para>
    /// </summary>
    private static bool TryReadTagsResponse(string body, out IReadOnlyList<string> models)
    {
        models = [];

        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var names = new List<string>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    names.Add(value);
                }
            }

            models = names;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the <c>/api/tags</c> URI, preserving any base path the instance is mounted under (an
    /// Ollama behind a reverse proxy at <c>/ollama</c> must be probed at <c>/ollama/api/tags</c>),
    /// the same way <c>SourceConnectivityProber.BuildCapsUri</c> does. No query string and no
    /// credential: this URI carries nothing sensitive, which is why the client it runs on needs no
    /// <c>.RemoveAllLoggers()</c> — see the registration comment in Program.cs.
    /// </summary>
    private static Uri BuildTagsUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/api/tags",
            Query = string.Empty,
        };

        return builder.Uri;
    }

    /// <summary>
    /// arb-1rr: the probe's own <c>/api/chat</c> request shape, deliberately LOCAL to this class
    /// rather than shared with <c>OllamaClient</c>'s record.
    ///
    /// <para><b>Why it is not shared.</b> That record lives in Arbitarr.Ai, which Arbitarr.Core does
    /// not reference (AC6, <c>CoreIsolationTests</c>). The shared thing that matters is the one the
    /// backend VALIDATES — <see cref="VerdictSchema"/>, which moved to Core so both send one
    /// constant — and the envelope around it is four field names Ollama's API fixes anyway.</para>
    ///
    /// <para><b>There is deliberately no <c>keep_alive</c>.</b> Getting that field's wire shape right
    /// is subtle (a bare integer must be a JSON number, not a string). Since arb-43b the single
    /// implementation of that rule is <see cref="OllamaKeepAlive"/>, here in Core, so sending the
    /// field would no longer duplicate anything — the original objection is gone. It stays omitted
    /// for the reason that is about this probe rather than about the rule: letting Ollama apply its
    /// own default is right for a one-shot probe that is not trying to keep a model resident. The
    /// honest tradeoff is unchanged — this probe would not itself have caught a keep_alive-only
    /// fault.</para>
    /// </summary>
    private sealed record ProbeChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ProbeChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] JsonElement Format);

    private sealed record ProbeChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    /// <summary>
    /// arb-1rr: the <c>/api/chat</c> URI, built exactly as <see cref="BuildTagsUri"/> builds its
    /// own and for the same reason — a base path must be preserved, so an Ollama mounted at
    /// <c>/ollama</c> is probed at <c>/ollama/api/chat</c>. Matches <c>OllamaClient.BuildChatUri</c>,
    /// so the probe and the classifier cannot disagree about which address they are testing.
    /// </summary>
    private static Uri BuildChatUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/api/chat",
            Query = string.Empty,
        };

        return builder.Uri;
    }
}
