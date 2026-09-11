using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Ai;

/// <summary>
/// <see cref="IOllamaClient"/> implementation calling a local Ollama instance's <c>/api/chat</c>
/// endpoint with <c>stream: false</c> and a constrained-decoding JSON Schema <c>format</c> (never
/// the older free-text <c>"json"</c> mode string), so the response always parses as exactly the
/// shape <see cref="VerdictSchema"/> declares.
///
/// <para>
/// Concurrency is capped at <see cref="OllamaOptions.MaxInFlight"/> (1) via a semaphore: Ollama
/// serializes inference by default (docs/step0-measurements.md §1), so more than one in-flight
/// request only stacks queued wall time without adding throughput. Each call gets its own
/// <see cref="OllamaOptions.CallTimeout"/> (5s) budget, and the whole call is gated by
/// <see cref="IAsyncCircuitBreaker"/> the same way <c>NzbHydraSource</c> gates upstream calls —
/// fail-open behavior (falling back to deterministic-only filtering when the breaker is open) is
/// the caller's responsibility (the AI verdict cache/chain), matching M5-3's fail-open requirement.
/// </para>
/// </summary>
public sealed class OllamaClient : IOllamaClient
{
    private const string SourceName = "Ollama";

    private readonly OllamaOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly SemaphoreSlim _inFlightGate;
    private readonly Func<CancellationToken, ValueTask<Uri>> _resolveBaseUrl;
    private readonly Func<CancellationToken, ValueTask<string>> _resolveModel;

    /// <param name="resolveBaseUrl">
    /// #89: resolves the base URL PER CALL rather than pinning it at construction, which is what
    /// makes a base-URL change take effect without restarting the host. Optional: when omitted the
    /// address falls back to <see cref="OllamaOptions.BaseUrl"/>, which is the shape every existing
    /// test constructs and the honest behaviour for a caller that has no settings store behind it.
    ///
    /// <para><b>Why the URL is no longer set as <see cref="HttpClient.BaseAddress"/>.</b> That
    /// property is write-once in practice (it may not be changed after the first request), and the
    /// client here comes from <c>IHttpClientFactory</c>, whose handlers are pooled and reused across
    /// scopes — so a BaseAddress captured on one scope's first call would outlive the setting it was
    /// read from and silently pin the old address. Building an ABSOLUTE request URI per call has no
    /// such lifetime, and it is why <c>ClassifyAsync</c> posts to a resolved absolute URI rather
    /// than to the relative "/api/chat" it used before.</para>
    /// </param>
    /// <param name="resolveModel">
    /// #112: resolves the MODEL per call, for exactly the same reason and with exactly the same
    /// fallback shape as <paramref name="resolveBaseUrl"/>. Before #112 the model was pinned in
    /// <see cref="OllamaOptions.Model"/> at start-up, so an operator whose instance had never pulled
    /// it could see a green connectivity test while every classification failed open — and could not
    /// correct it without recycling the process. Optional: when omitted the model falls back to
    /// <see cref="OllamaOptions.Model"/>, which is the shape every existing test constructs and the
    /// honest behaviour for a caller with no settings store behind it.
    /// </param>
    public OllamaClient(
        OllamaOptions options,
        HttpClient httpClient,
        IAsyncCircuitBreaker circuitBreaker,
        Func<CancellationToken, ValueTask<Uri>>? resolveBaseUrl = null,
        Func<CancellationToken, ValueTask<string>>? resolveModel = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _inFlightGate = new SemaphoreSlim(OllamaOptions.MaxInFlight, OllamaOptions.MaxInFlight);
        _resolveBaseUrl = resolveBaseUrl ?? (_ => ValueTask.FromResult(options.BaseUrl));
        _resolveModel = resolveModel ?? (_ => ValueTask.FromResult(options.Model));

        // M5 security review (MED): bound the response body size the underlying handler will
        // buffer — Ollama is local/trusted infrastructure, but a misbehaving or misconfigured
        // endpoint returning an unbounded body should not be able to pressure process memory.
        // 64 KiB is comfortably above any real verdict JSON payload.
        _httpClient.MaxResponseContentBufferSize = 64 * 1024;
    }

    public async Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!await _circuitBreaker.CanCallAsync(SourceName, cancellationToken).ConfigureAwait(false))
        {
            throw new OllamaCircuitOpenException();
        }

        await _inFlightGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(OllamaOptions.CallTimeout);

            var messages = ClassificationPrompt.Build(candidate)
                .Select(m => new OllamaChatRequestMessage(m.Role, m.Content))
                .ToArray();

            try
            {
                // #89/#112: BOTH the address and the model are resolved here, inside the call, so an
                // operator who changes either in the settings UI has the NEXT classification use it.
                // Resolution happens before the circuit breaker records anything, and a failure to
                // resolve is a genuine call failure like any other. The request is built AFTER the
                // resolution rather than before it, which is why it moved inside this block — built
                // above, it would have carried the model that was in force when the method was
                // entered and silently reintroduced the restart requirement for that field alone.
                var baseUrl = await _resolveBaseUrl(timeoutCts.Token).ConfigureAwait(false);
                var model = await _resolveModel(timeoutCts.Token).ConfigureAwait(false);
                var chatUri = BuildChatUri(baseUrl);

                var request = new OllamaChatRequest(
                    model,
                    messages,
                    Stream: false,
                    Format: JsonDocument.Parse(VerdictSchema.Object).RootElement.Clone(),
                    KeepAlive: _options.KeepAlive);

                using var response = await _httpClient
                    .PostAsJsonAsync(chatUri, request, JsonOptions, timeoutCts.Token)
                    .ConfigureAwait(false);

                // arb-1rr: NOT EnsureSuccessStatusCode(). That throws with the body already
                // discarded, so a 400 reached the circuit breaker as a bare
                // "HttpRequestException (400 BadRequest)" and the operator never saw WHICH of the
                // many 400s Ollama serves it was (a rejected option, an unknown model, a schema it
                // would not accept). Ollama puts that reason in the response body and nowhere else,
                // so it is read here, while the response is still open, and carried on the
                // exception. SanitizedErrorDescription decides what of it may reach an
                // unauthenticated surface; this layer's job is only to stop throwing it away.
                if (!response.IsSuccessStatusCode)
                {
                    throw await OllamaRequestException
                        .FromResponseAsync(response, timeoutCts.Token)
                        .ConfigureAwait(false);
                }

                var payload = await response.Content
                    .ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (payload?.Message?.Content is not { Length: > 0 } content)
                {
                    throw new InvalidOperationException("Ollama response had no message content.");
                }

                var verdict = JsonSerializer.Deserialize<OllamaVerdictPayload>(content, JsonOptions)
                    ?? throw new InvalidOperationException("Ollama response content did not match the verdict schema.");

                await _circuitBreaker.RecordSuccessAsync(SourceName, cancellationToken).ConfigureAwait(false);
                return new OllamaVerdict(verdict.Verdict, verdict.Confidence);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await _circuitBreaker.RecordFailureAsync(SourceName, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _inFlightGate.Release();
        }
    }

    /// <summary>
    /// Appends <c>/api/chat</c> to the configured base URL, PRESERVING any path it is mounted under
    /// — an Ollama behind a reverse proxy at <c>/ollama</c> must be called at
    /// <c>/ollama/api/chat</c>. <c>new Uri(baseUrl, "/api/chat")</c> would silently discard that
    /// prefix, since a leading slash makes the relative part root-anchored; this is the same
    /// construction <c>OllamaConnectivityProber</c> uses, so the probe and the classifier cannot
    /// disagree about which address they are talking to.
    /// </summary>
    private static Uri BuildChatUri(Uri baseUrl) =>
        new UriBuilder(baseUrl)
        {
            Path = baseUrl.AbsolutePath.TrimEnd('/') + "/api/chat",
            Query = string.Empty,
        }.Uri;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatRequestMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")] JsonElement Format,
        [property: JsonPropertyName("keep_alive")]
        [property: JsonConverter(typeof(OllamaKeepAliveJsonConverter))]
        OllamaKeepAlive KeepAlive);

    /// <summary>
    /// Adapts <see cref="OllamaKeepAlive"/> to System.Text.Json. The wire shape itself — a JSON
    /// NUMBER for a bare integer, a JSON STRING for a unit-bearing Go duration, and why the two must
    /// differ — is <see cref="OllamaKeepAlive.WriteTo"/>'s, and is documented on that type. This
    /// class deliberately holds no rule of its own: arb-43b removed the second copy of that rule
    /// that used to live here.
    /// </summary>
    private sealed class OllamaKeepAliveJsonConverter : JsonConverter<OllamaKeepAlive>
    {
        public override OllamaKeepAlive Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // A number on the wire is the bare-integer form; render it back to its text so the value
            // round-trips to the same spelling it was written from.
            var text = reader.TokenType == JsonTokenType.Number
                ? reader.GetInt64().ToString(CultureInfo.InvariantCulture)
                : reader.GetString();

            // An unparseable value degrades to the default rather than throwing, matching what the
            // old converter did with an unrecognised string (it wrote it through verbatim rather
            // than failing). Nothing in this application deserialises a request body; this exists so
            // the converter is total.
            return OllamaKeepAlive.TryParse(text, out var value) ? value : OllamaKeepAlive.Default;
        }

        public override void Write(Utf8JsonWriter writer, OllamaKeepAlive value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            value.WriteTo(writer);
        }
    }

    private sealed record OllamaChatRequestMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaChatResponseMessage? Message);

    private sealed record OllamaChatResponseMessage(
        [property: JsonPropertyName("content")] string? Content);

    private sealed record OllamaVerdictPayload(
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("confidence")] double Confidence);
}

/// <summary>Thrown by <see cref="OllamaClient.ClassifyAsync"/> when the circuit breaker is open (fail-fast, not fail-inline).</summary>
public sealed class OllamaCircuitOpenException : Exception
{
    public OllamaCircuitOpenException()
        : base("Ollama circuit breaker is open; call rejected without contacting the model.")
    {
    }
}
