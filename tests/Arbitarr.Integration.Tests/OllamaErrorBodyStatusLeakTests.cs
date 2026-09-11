using System.Text.RegularExpressions;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-1rr: the planted-secret test for the NEW path — an Ollama error body reaching the
/// unauthenticated <c>GET /api/status</c> as a source's <c>lastError</c>.
///
/// <para><b>Why this exists when <c>ConfigMaskingTests</c> already tests /api/status.</b> That test
/// says so itself: it seeds <c>LastError</c> with a value that is ALREADY sanitized, and explicitly
/// defers the capture point as "SourceCircuitBreakerTests' concern, not this endpoint-level test's".
/// That division was sound while no upstream text could reach the field at all — the outcome was a
/// closed enum and <c>Describe</c> returned a type name. arb-1rr changed that by admitting a body
/// excerpt, so the question "could a secret in an Ollama response body reach the dashboard" is now
/// a real one, and no existing test drives it end to end.</para>
///
/// <para><b>The real path, not a fake.</b> The exception is a real
/// <see cref="OllamaRequestException"/> built from a body carrying a planted host and a planted key,
/// handed to the REAL <see cref="SourceCircuitBreaker"/> resolved from the composed host, and read
/// back off the REAL unauthenticated endpoint. Nothing here stubs the scrubber, so a regression
/// anywhere along that chain — the exception keeping raw text, <c>Describe</c> dropping its scrub,
/// the breaker bypassing <c>Describe</c> — fails this test.</para>
///
/// <para><b>Every assertion carries a positive control.</b> Each planted value is first shown
/// findable by the same search that then asserts its absence, and the response is shown to actually
/// CONTAIN the sanitized error — so the absence assertions cannot pass because the error never
/// arrived (CLAUDE.md §4: an empty set contains nothing).</para>
///
/// <para>Host is an RFC 2606 documentation name; the key is an obvious placeholder.</para>
/// </summary>
public sealed partial class OllamaErrorBodyStatusLeakTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string PlantedHost = "ollama.internal.example";
    private const string PlantedKey = "PLANTEDSTATUSKEY13579";
    private const string SourceName = "Ollama";

    private readonly ArbitarrWebApplicationFactory _factory;

    public OllamaErrorBodyStatusLeakTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task An_ollama_error_body_reaches_status_without_its_host_or_key()
    {
        // The body Ollama might return with a 400, carrying both a host and a credential — either
        // because the operator's request echoed one back or because the backend mentioned its peer.
        var leakyBody =
            $$"""{"error":"upstream {{PlantedHost}}:11434 rejected apikey={{PlantedKey}}: time: missing unit in duration \"-1\""}""";

        // DETECTABILITY CONTROL: both values are genuinely in the input, and these very searches
        // find them there. Without this, the absence assertions below could pass against text that
        // never contained them.
        Assert.Contains(PlantedHost, leakyBody, StringComparison.Ordinal);
        Assert.Contains(PlantedKey, leakyBody, StringComparison.Ordinal);

        var exception = new OllamaRequestException(System.Net.HttpStatusCode.BadRequest, leakyBody);

        // The REAL persistence path, not the in-memory breaker: IAsyncCircuitBreaker is exactly what
        // OllamaClient.ClassifyAsync hands a failure to, and it is what writes the SourceHealthRecord
        // row that /api/status serves. Recording against the in-memory singleton alone would leave
        // the endpoint reading an empty table and the whole test passing vacuously.
        using (var scope = _factory.Services.CreateScope())
        {
            var breaker = scope.ServiceProvider.GetRequiredService<IAsyncCircuitBreaker>();
            await breaker.RecordFailureAsync(SourceName, exception);
        }

        using var client = _factory.CreateClient();
        var body = await client.GetStringAsync("/api/status");

        // NON-VACUITY: the error really did travel to the dashboard. Everything below is about what
        // it carried, and would be meaningless if it had not arrived at all.
        Assert.Contains("400", body, StringComparison.Ordinal);
        Assert.Contains("missing unit in duration", body, StringComparison.Ordinal);

        // THE PROPERTY: neither the host nor the key survived the trip.
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

    /// <summary>
    /// The same guarantee one layer earlier: the exception's own MESSAGE is clean, so the raw body
    /// cannot reach the persistent log store through a generic handler that logs it — a path no
    /// display-side scrubber sits on.
    /// </summary>
    [Fact]
    public void The_exception_message_carries_no_planted_secret()
    {
        var leakyBody = $$"""{"error":"{{PlantedHost}}:11434 apikey={{PlantedKey}}"}""";

        Assert.Contains(PlantedHost, leakyBody, StringComparison.Ordinal);
        Assert.Contains(PlantedKey, leakyBody, StringComparison.Ordinal);

        var exception = new OllamaRequestException(System.Net.HttpStatusCode.BadRequest, leakyBody);

        Assert.DoesNotContain(PlantedHost, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedHost, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedKey, exception.ToString(), StringComparison.Ordinal);
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
