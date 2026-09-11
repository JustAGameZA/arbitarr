using System.Net;
using System.Text.RegularExpressions;
using Arbitarr.Ai;
using Arbitarr.Core.Releases;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-1rr: the end-to-end proof that a <c>400</c> from <c>/api/chat</c> DURING CLASSIFICATION
/// reaches the unauthenticated <c>GET /api/status</c> as a source <c>lastError</c> that says WHICH
/// 400 it was, while carrying neither the upstream host nor a credential.
///
/// <para><b>Why this is not covered by the neighbouring tests.</b> Three tests now touch this area
/// and none of them drives this path: <c>OllamaErrorBodyStatusLeakTests</c> constructs the exception
/// itself and hands it to the breaker, so it pins the scrubbing but not the capture;
/// <c>AdminAiEndpointsTests</c> exercises the PROBE, which is a different request from a different
/// component; and <c>ConfigMaskingTests</c> seeds <c>LastError</c> with an already-sanitized value
/// and says in its own comment that the capture point is somebody else's concern. The question this
/// file answers — does a real Ollama error body, read off a real response by
/// <see cref="OllamaClient"/> on the real classification path, arrive on the dashboard useful and
/// clean — is therefore asked nowhere else.</para>
///
/// <para><b>The only thing faked is Ollama itself.</b> The host is the real composition root; the
/// client is the real scoped <see cref="IOllamaClient"/> Program.cs registers, with its real
/// circuit breaker, its real settings resolvers, and the real endpoint serving the result. A stub
/// primary handler stands in for the Ollama server because the test must choose the response body —
/// that is the input under test, not part of the machinery being verified.</para>
///
/// <para><b>Both planted values carry a positive control</b> (CLAUDE.md §4): each is first shown
/// findable by the same search that later asserts its absence, and the response is separately shown
/// to contain the real reason — so no absence assertion here can pass because the error never
/// arrived.</para>
///
/// <para>Host is an RFC 2606 documentation name and the address an RFC 5737 documentation address;
/// the key is an obvious placeholder.</para>
/// </summary>
public sealed partial class OllamaClassificationErrorStatusTests
{
    private const string PlantedHost = "ollama.internal.example";
    private const string PlantedKey = "PLANTEDCLASSIFYKEY24680";

    /// <summary>The distinctive part of Ollama's reason — what an operator actually needs to see.</summary>
    private const string UsefulReason = "model \\\"phi4:14b\\\" not found, try pulling it first";

    [Fact]
    public async Task A_chat_400_during_classification_reaches_status_with_its_reason_but_no_host_or_key()
    {
        // The body a real Ollama might return with a 400, carrying the useful reason alongside a
        // host and a credential — the shape this feature deliberately admits, minus what it must strip.
        var errorBody =
            $$"""{"error":"upstream {{PlantedHost}}:11434 rejected apikey={{PlantedKey}}: {{UsefulReason}}"}""";

        // DETECTABILITY CONTROL: both planted values really are in what Ollama "returns", found by
        // the very searches used against the response below.
        Assert.Contains(PlantedHost, errorBody, StringComparison.Ordinal);
        Assert.Contains(PlantedKey, errorBody, StringComparison.Ordinal);

        using var factory = new ArbitarrWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                // Replaces ONLY the transport for the named Ollama client. Everything downstream of
                // the response — reading the body, building the exception, recording the failure,
                // projecting it onto /api/status — remains the code Program.cs composed.
                services.AddHttpClient(nameof(OllamaClient))
                    .ConfigurePrimaryHttpMessageHandler(() =>
                        new StubOllamaHandler(HttpStatusCode.BadRequest, errorBody))));

        using (var scope = configured.Services.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<IOllamaClient>();

            // The real classification call. It is expected to throw: the point is what it records on
            // its way out, not its return value.
            await Assert.ThrowsAsync<Arbitarr.Core.Ai.OllamaRequestException>(
                () => client.ClassifyAsync(Candidate()));
        }

        using var httpClient = configured.CreateClient();
        var body = await httpClient.GetStringAsync("/api/status");

        // NON-VACUITY: the failure really did travel to the dashboard, and it carried the detail
        // that makes this bead worth shipping — without it every 400 still reads alike.
        Assert.Contains("400", body, StringComparison.Ordinal);
        Assert.Contains("not found, try pulling it first", body, StringComparison.Ordinal);

        // SCRUBBING CONTROL (CLAUDE.md §4): the redaction token is PRESENT, proving the planted
        // values reached the scrubber and were replaced there — not that they never arrived. An
        // absence assertion alone would pass just as happily on a body the excerpt never reached.
        Assert.Contains(
            Arbitarr.Core.Diagnostics.SanitizedErrorDescription.Replacement,
            body,
            StringComparison.Ordinal);

        // THE PROPERTY: the topology and the credential did not come with it.
        Assert.DoesNotContain(PlantedHost, body, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain("11434", body, StringComparison.Ordinal);
        Assert.False(
            PrivateLanAddressPattern().IsMatch(body),
            $"Status body matched an RFC 1918 address pattern: {body}");
        Assert.False(
            CredentialLikePattern().IsMatch(body),
            $"Status body matched a credential pattern: {body}");
    }

    private static ReleaseCandidate Candidate() => new()
    {
        Title = "Example.Show.S01E01.1080p.WEB-DL",
        Guid = "arb-1rr-classification-status",
        PubDate = DateTimeOffset.UnixEpoch,
        Link = new Uri("https://releases.example.invalid/arb-1rr"),
    };

    /// <summary>Answers every request with one fixed status and body, standing in for Ollama.</summary>
    private sealed class StubOllamaHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }

    // Mirrors .githooks/pre-commit's RFC 1918 detector (192.168.x.x / 10.x.x.x / 172.16-31.x.x).
    [GeneratedRegex(@"\b(192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2[0-9]|3[01])\.\d{1,3}\.\d{1,3})\b")]
    private static partial Regex PrivateLanAddressPattern();

    // Mirrors .githooks/pre-commit's credential-looking-value detector.
    [GeneratedRegex(
        @"(apikey|api_key|password|passwd|secret|token)[""'\s]*[=:]\s*[""']?[A-Za-z0-9+/_-]{16,}",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialLikePattern();
}
