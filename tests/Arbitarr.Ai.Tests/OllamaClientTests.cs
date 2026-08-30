using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task ClassifyAsync_RequestBody_ForwardsKeepAlive()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        Assert.Equal("-1", body.RootElement.GetProperty("keep_alive").GetString());
    }

    [Fact]
    public async Task ClassifyAsync_RequestBody_IncludesModelName()
    {
        var (client, handler, _) = CreateClient(SuccessResponse());

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        Assert.Equal("test-model:latest", body.RootElement.GetProperty("model").GetString());
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
