using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public OllamaClient(
        OllamaOptions options,
        HttpClient httpClient,
        IAsyncCircuitBreaker circuitBreaker,
        Func<CancellationToken, ValueTask<Uri>>? resolveBaseUrl = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _inFlightGate = new SemaphoreSlim(OllamaOptions.MaxInFlight, OllamaOptions.MaxInFlight);
        _resolveBaseUrl = resolveBaseUrl ?? (_ => ValueTask.FromResult(options.BaseUrl));

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

            var request = new OllamaChatRequest(
                _options.Model,
                ClassificationPrompt.Build(candidate).Select(m => new OllamaChatRequestMessage(m.Role, m.Content)).ToArray(),
                Stream: false,
                Format: JsonDocument.Parse(VerdictSchema.Object).RootElement.Clone(),
                KeepAlive: _options.KeepAlive);

            try
            {
                // #89: resolved here, inside the call, so an operator who changes the base URL in
                // the settings UI has the NEXT classification use it. Resolution happens before the
                // circuit breaker records anything, and a failure to resolve is a genuine call
                // failure like any other.
                var baseUrl = await _resolveBaseUrl(timeoutCts.Token).ConfigureAwait(false);
                var chatUri = BuildChatUri(baseUrl);

                using var response = await _httpClient
                    .PostAsJsonAsync(chatUri, request, JsonOptions, timeoutCts.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

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
        [property: JsonPropertyName("keep_alive")] string KeepAlive);

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
