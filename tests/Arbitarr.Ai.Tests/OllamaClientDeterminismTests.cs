using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// arb-p4r: <see cref="OllamaClient"/> pins <c>options.temperature</c>/<c>options.seed</c> on the
/// chat request so the same title yields the same verdict run to run (F-009) — without this, the
/// verdict cache (keyed on model name + digest + <c>PromptVersion</c>) cannot be reproducible.
/// These tests assert the request shape carries the fixed sampling options, and that a small
/// recorded corpus parses to the expected verdict/confidence through the same stub-handler path
/// <see cref="OllamaClientTests"/> uses.
/// </summary>
public class OllamaClientDeterminismTests
{
    private static ReleaseCandidate Candidate(string title = "Movie.2024.1080p.WEB-DL") => new()
    {
        Title = title,
        Guid = "guid-1",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
        Protocol = ProtocolKind.Torrent,
    };

    private static (OllamaClient Client, RecordingHandler Handler) CreateClient(HttpResponseMessage response)
    {
        var handler = new RecordingHandler(response);
        var httpClient = new HttpClient(handler);
        var breaker = new AlwaysClosedCircuitBreaker();
        var options = new OllamaOptions(new Uri("http://192.0.2.138:31434"), "test-model:latest");
        var client = new OllamaClient(options, httpClient, breaker);
        return (client, handler);
    }

    private static HttpResponseMessage SuccessResponse(string verdict, double confidence)
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
    public async Task ClassifyAsync_RequestBody_PinsTemperatureAndSeed()
    {
        var (client, handler) = CreateClient(SuccessResponse("accept", 0.95));

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        var options = body.RootElement.GetProperty("options");
        Assert.Equal(0, options.GetProperty("temperature").GetDouble());
        Assert.Equal(42, options.GetProperty("seed").GetInt32());
    }

    [Fact]
    public async Task ClassifyAsync_RequestBody_StillIncludesStreamAndFormat_AlongsideOptions()
    {
        var (client, handler) = CreateClient(SuccessResponse("accept", 0.95));

        await client.ClassifyAsync(Candidate());

        var body = await handler.LastRequestBodyAsync();
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("format").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("options").ValueKind);
    }

    public sealed record GoldenCorpusEntry(string Title, string Verdict, double Confidence);

    /// <summary>
    /// A small recorded fixture (synthetic titles only — no real release names, no URLs/hosts) run
    /// through the same stub-handler path as <see cref="OllamaClientTests"/>, asserting the parsed
    /// verdict/confidence for each entry matches what was recorded.
    /// </summary>
    public static IEnumerable<object[]> GoldenCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "classifier-golden-corpus.json");
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<GoldenCorpusEntry>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Golden corpus fixture parsed to null.");

        foreach (var entry in entries)
        {
            yield return [entry];
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCorpus))]
    public async Task ClassifyAsync_GoldenCorpusEntry_ParsesExpectedVerdictAndConfidence(GoldenCorpusEntry entry)
    {
        var (client, _) = CreateClient(SuccessResponse(entry.Verdict, entry.Confidence));

        var result = await client.ClassifyAsync(Candidate(entry.Title));

        Assert.Equal(entry.Verdict, result.Verdict);
        Assert.Equal(entry.Confidence, result.Confidence);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private string? _lastRequestBody;

        public RecordingHandler(HttpResponseMessage response) => _response = response;

        public async Task<JsonDocument> LastRequestBodyAsync() =>
            JsonDocument.Parse(_lastRequestBody ?? throw new InvalidOperationException("No request captured."));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _lastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _response;
        }
    }

    private sealed class AlwaysClosedCircuitBreaker : IAsyncCircuitBreaker
    {
        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
