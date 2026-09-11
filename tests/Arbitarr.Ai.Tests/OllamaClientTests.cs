using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// Step 1: <see cref="OllamaClient"/> request-shape and behavioral contract — <c>/api/chat</c>,
/// <c>stream:false</c>, a JSON-Schema <c>format</c> (constrained decoding, not the legacy
/// <c>"json"</c> string mode), <c>keep_alive</c> forwarded from <see cref="OllamaOptions"/>, the
/// R17-relevant model-name cache-key sensitivity, and circuit-breaker gating.
/// </summary>
public class OllamaClientTests
{
    private static ReleaseCandidate Candidate() => new()
    {
        Title = "Movie.2024.1080p.WEB-DL",
        Guid = "guid-1",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
        Protocol = ProtocolKind.Torrent,
    };

    private static (OllamaClient Client, RecordingHandler Handler, AlwaysClosedCircuitBreaker Breaker) CreateClient(
        HttpResponseMessage response)
    {
        var handler = new RecordingHandler(response);
        var httpClient = new HttpClient(handler);
        var breaker = new AlwaysClosedCircuitBreaker();
        var options = new OllamaOptions(new Uri("http://192.0.2.138:31434"), "test-model:latest");
        var client = new OllamaClient(options, httpClient, breaker);
        return (client, handler, breaker);
    }

    private static HttpResponseMessage SuccessResponse(string verdict = "accept", double confidence = 0.95)
    {
        var payload = new
        {
            message = new
            {
                content = JsonSerializer.Serialize(new { verdict, confidence }),
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload),
        };
    }

    [Fact]
    public async Task ClassifyAsync_PostsToApiChatEndpoint()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        Assert.Equal("/api/chat", handler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ClassifyAsync_RequestBody_SetsStreamFalse()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ClassifyAsync_RequestBody_IncludesJsonSchemaFormat_NotLegacyJsonString()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        var format = body.RootElement.GetProperty("format");
        Assert.Equal(JsonValueKind.Object, format.ValueKind);
        Assert.True(format.TryGetProperty("properties", out _));
    }

    [Fact]
    public async Task ClassifyAsync_RequestBody_ForwardsKeepAlive_AsJsonNumberByDefault()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        var keepAlive = body.RootElement.GetProperty("keep_alive");
        Assert.Equal(JsonValueKind.Number, keepAlive.ValueKind);
        Assert.Equal(-1, keepAlive.GetInt32());
    }

    [Theory]
    [InlineData("-1", JsonValueKind.Number)]
    [InlineData("300", JsonValueKind.Number)]
    [InlineData("-1m", JsonValueKind.String)]
    [InlineData("30m", JsonValueKind.String)]
    public async Task ClassifyAsync_RequestBody_KeepAlive_WireShapeMatchesInputForm(
        string keepAliveOption, JsonValueKind expectedKind)
    {
        var handler = new RecordingHandler(SuccessResponse());
        var httpClient = new HttpClient(handler);
        var breaker = new AlwaysClosedCircuitBreaker();
        // arb-43b: the option is now a validated value type. Parsed here rather than changing the
        // InlineData rows, so this test still drives the exact strings it always has.
        Assert.True(Arbitarr.Core.Ai.OllamaKeepAlive.TryParse(keepAliveOption, out var keepAliveValue));
        var options = new OllamaOptions(new Uri("http://192.0.2.138:31434"), "test-model:latest", keepAliveValue);
        var client = new OllamaClient(options, httpClient, breaker);

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        var keepAlive = body.RootElement.GetProperty("keep_alive");
        Assert.Equal(expectedKind, keepAlive.ValueKind);
        if (expectedKind == JsonValueKind.Number)
        {
            Assert.Equal(int.Parse(keepAliveOption), keepAlive.GetInt32());
        }
        else
        {
            Assert.Equal(keepAliveOption, keepAlive.GetString());
        }
    }

    [Fact]
    public async Task ClassifyAsync_RequestBody_IncludesModelName()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        Assert.Equal("test-model:latest", body.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// arb-p4r: <see cref="OllamaClient"/> pins <c>options.temperature</c>/<c>options.seed</c> on the
    /// chat request so the same title yields the same verdict run to run (F-009) — without this, the
    /// verdict cache (keyed on model name + digest + <c>PromptVersion</c>) cannot be reproducible.
    /// The fixed values themselves live on <see cref="OllamaOptions.SamplingTemperature"/>/
    /// <see cref="OllamaOptions.SamplingSeed"/>, so this test compares against those rather than
    /// restating the literals.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_RequestBody_PinsTemperatureAndSeed()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        var options = body.RootElement.GetProperty("options");
        Assert.Equal(OllamaOptions.SamplingTemperature, options.GetProperty("temperature").GetDouble());
        Assert.Equal(OllamaOptions.SamplingSeed, options.GetProperty("seed").GetInt32());
    }

    [Fact]
    public async Task ClassifyAsync_RequestBody_StillIncludesStreamAndFormat_AlongsideOptions()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("format").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("options").ValueKind);
    }

    /// <summary>
    /// arb-p4r: the theory this replaces stubbed the Ollama response FROM the same fixture row it
    /// then asserted back — the candidate's title never influenced the result, so it passed even
    /// with the deterministic <c>options</c> block deleted entirely. What actually needs to hold for
    /// the verdict cache to be reproducible is that classifying the SAME candidate twice produces a
    /// byte-identical serialized request body — if sampling (or anything else in the request) ever
    /// varied between two calls for the same input, the cache could not trust a hit to agree with a
    /// fresh call. Serializing the request twice and comparing bytes is a stronger assertion than
    /// re-parsing each side into a model, which would tolerate reordering.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_SameCandidateClassifiedTwice_ProducesByteIdenticalRequestBody()
    {
        // Two independently constructed clients/handlers rather than one reused HttpClient call
        // twice: HttpClient disposes the response's content after it is read once, so a single
        // stubbed HttpResponseMessage cannot serve a second request.
        var (firstClient, firstHandler, _) = CreateClient(SuccessResponse());
        var (secondClient, secondHandler, _) = CreateClient(SuccessResponse());
        var candidate = Candidate();

        await firstClient.ClassifyAsync(candidate);
        var firstBody = await firstHandler.LastRequestBodyStringAsync();

        await secondClient.ClassifyAsync(candidate);
        var secondBody = await secondHandler.LastRequestBodyStringAsync();

        Assert.Equal(firstBody, secondBody, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ClassifyAsync_ParsesVerdictAndConfidenceFromResponse()
    {
        var (client, _, _) = CreateClient(SuccessResponse("reject", 0.42));

        var result = await client.ClassifyAsync(Candidate());

        Assert.Equal("reject", result.Verdict);
        Assert.Equal(0.42, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_CircuitOpen_ThrowsWithoutCallingHttpClient()
    {
        var handler = new RecordingHandler(SuccessResponse());
        var httpClient = new HttpClient(handler);
        var breaker = new AlwaysOpenCircuitBreaker();
        var options = new OllamaOptions(new Uri("http://192.0.2.138:31434"), "test-model:latest");
        var client = new OllamaClient(options, httpClient, breaker);

        await Assert.ThrowsAsync<OllamaCircuitOpenException>(() => client.ClassifyAsync(Candidate()));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ClassifyAsync_HttpFailure_RecordsCircuitBreakerFailure()
    {
        var (client, _, breaker) = CreateClient(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAnyAsync<Exception>(() => client.ClassifyAsync(Candidate()));

        Assert.True(breaker.FailureRecorded);
    }

    [Fact]
    public async Task ClassifyAsync_OversizedResponseBody_IsBoundedNotUnbounded()
    {
        // M5 security review (MED): OllamaClient.MaxResponseContentBufferSize caps the response
        // body buffered into memory. A malicious/misbehaving Ollama endpoint returning a body far
        // larger than any legitimate verdict payload must not be buffered without limit; it should
        // fail (HttpRequestException from exceeding MaxResponseContentBufferSize) rather than the
        // client accepting and buffering an unbounded response.
        var oversizedContent = new string('x', 128 * 1024); // 128KB > 64KB cap
        var payload = new
        {
            message = new
            {
                content = JsonSerializer.Serialize(new { verdict = "accept", confidence = 0.9, note = oversizedContent }),
            },
        };
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
        var (client, _, _) = CreateClient(response);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ClassifyAsync(Candidate()));
    }

    // -------------------------------------------------------------------------------------------
    // arb-1rr: the response body of a FAILED call is captured rather than discarded. Ollama reports
    // why it rejected a request only in that body, and EnsureSuccessStatusCode threw it away — so
    // every distinct 400 reached the dashboard as one indistinguishable
    // "HttpRequestException (400 BadRequest)".
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A non-2xx throws <see cref="OllamaRequestException"/> carrying BOTH the status and the
    /// upstream reason. Deriving from <see cref="HttpRequestException"/> is asserted too: every
    /// existing catch site matches on that type, so losing the inheritance would silently change
    /// which failures the circuit breaker records.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_NonSuccessResponse_ThrowsWithTheStatusAndTheBodyExcerpt()
    {
        const string body = """{"error":"time: missing unit in duration \"-1\""}""";
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body),
        };
        var (client, _, _) = CreateClient(response);

        var ex = await Assert.ThrowsAsync<OllamaRequestException>(() => client.ClassifyAsync(Candidate()));

        Assert.IsAssignableFrom<HttpRequestException>(ex);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("missing unit in duration", ex.BodyExcerpt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The excerpt is CAPPED. This value is persisted per source and served on a dashboard, so a
    /// misbehaving endpoint must not be able to push an arbitrarily long string into either. The
    /// assertion is on the exact cap rather than "shorter than the body", which a truncation to any
    /// length would satisfy.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_NonSuccessResponse_TruncatesALongBodyToTheCap()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new string('x', 1024)),
        };
        var (client, _, _) = CreateClient(response);

        var ex = await Assert.ThrowsAsync<OllamaRequestException>(() => client.ClassifyAsync(Candidate()));

        Assert.Equal(OllamaRequestException.MaxExcerptLength, ex.BodyExcerpt.Length);
    }

    /// <summary>
    /// The failure still reaches the circuit breaker. Changing WHAT is thrown must not change
    /// WHETHER the breaker trips — that is the behaviour every fail-open path downstream depends on.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_NonSuccessResponse_StillRecordsACircuitBreakerFailure()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"nope"}"""),
        };
        var (client, _, breaker) = CreateClient(response);

        await Assert.ThrowsAsync<OllamaRequestException>(() => client.ClassifyAsync(Candidate()));

        Assert.True(breaker.FailureRecorded);
    }

    [Fact]
    public void MaxInFlight_IsOne()
    {
        Assert.Equal(1, OllamaOptions.MaxInFlight);
    }

    [Fact]
    public void CallTimeout_IsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), OllamaOptions.CallTimeout);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private string? _lastRequestBody;

        public RecordingHandler(HttpResponseMessage response) => _response = response;

        public HttpRequestMessage? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public async Task<JsonDocument> LastRequestBodyAsync() =>
            JsonDocument.Parse(_lastRequestBody ?? throw new InvalidOperationException("No request captured."));

        public Task<string> LastRequestBodyStringAsync() =>
            Task.FromResult(_lastRequestBody ?? throw new InvalidOperationException("No request captured."));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            _lastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _response;
        }
    }

    private sealed class AlwaysClosedCircuitBreaker : IAsyncCircuitBreaker
    {
        public bool FailureRecorded { get; private set; }

        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default)
        {
            FailureRecorded = true;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysOpenCircuitBreaker : IAsyncCircuitBreaker
    {
        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
