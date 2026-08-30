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

    public OllamaClient(OllamaOptions options, HttpClient httpClient, IAsyncCircuitBreaker circuitBreaker)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _inFlightGate = new SemaphoreSlim(OllamaOptions.MaxInFlight, OllamaOptions.MaxInFlight);

        _httpClient.BaseAddress ??= options.BaseUrl;

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
                using var response = await _httpClient
                    .PostAsJsonAsync("/api/chat", request, JsonOptions, timeoutCts.Token)
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
