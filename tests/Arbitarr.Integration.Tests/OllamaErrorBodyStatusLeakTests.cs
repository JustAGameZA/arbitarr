using System.Net;
using System.Text.RegularExpressions;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Settings;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data.Entities;
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
///
/// <para><b>arb-mhd2 moved the surface under test, not the property.</b> <c>GET /api/status</c> no
/// longer publishes the error text at all: it publishes a closed outcome, and the sanitized detail
/// moved behind the admin key on <c>GET /api/admin/status/diagnostics</c>. So the positive controls
/// that used to read the excerpt off the public body now read it off the ADMIN body, and the public
/// body is asserted to carry neither the excerpt nor the planted values. That is a strictly stronger
/// statement than before -- previously the excerpt was public and only the secrets inside it were
/// scrubbed; now the excerpt is gated AND still scrubbed, so a regression in either fails here.</para>
/// </summary>
public sealed partial class OllamaErrorBodyStatusLeakTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string PlantedHost = "ollama.internal.example";
    private const string PlantedKey = "PLANTEDSTATUSKEY13579";
    private const string SourceName = "Ollama";

    /// <summary>arb-mhd2: the admin-gated read the sanitized detail moved to.</summary>
    private const string DiagnosticsRoute = "/api/admin/status/diagnostics";
    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public OllamaErrorBodyStatusLeakTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Upsert, not Add: the factory's SQLite database is shared across every [Fact] in this
    // IClassFixture-scoped class, so a second seed of the same primary key would collide.
    private async Task SeedAdminKeyAsync()
    {
        await _factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = AdminKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = AdminKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    private async Task<string> GetDiagnosticsBodyAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DiagnosticsRoute);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
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

        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        // POSITIVE CONTROL FIRST (arb-mhd2). The excerpt is read off the ADMIN body, which is where
        // it now lives. This is what makes every absence assertion on the public body below bite:
        // it proves the error genuinely travelled the whole chain and is findable by these very
        // searches, so the public body carrying none of it is a real gating result rather than an
        // assertion over an error that never arrived.
        var adminBody = await GetDiagnosticsBodyAsync(client);
        Assert.Contains("400", adminBody, StringComparison.Ordinal);
        Assert.Contains("missing unit in duration", adminBody, StringComparison.Ordinal);

        // SCRUBBING CONTROL (CLAUDE.md §4, added by arb-fbx): the redaction token is PRESENT, which
        // is what distinguishes "the planted values reached the scrubber and were replaced" from
        // "the excerpt never arrived". Gating did not replace scrubbing -- the admin body is still
        // sanitized, because an admin reader has no business seeing an upstream's credentials either.
        Assert.Contains(
            Arbitarr.Core.Diagnostics.SanitizedErrorDescription.Replacement,
            adminBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedHost, adminBody, StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedKey, adminBody, StringComparison.Ordinal);

        var body = await client.GetStringAsync("/api/status");

        // NON-VACUITY for the public body: the failure IS reported there, as a closed outcome. So
        // the absence of the text below is the gating working, not the source having gone missing
        // from the response entirely.
        using var status = System.Text.Json.JsonDocument.Parse(body);
        var sources = status.RootElement.GetProperty("sources").EnumerateArray().ToList();
        Assert.NotEmpty(sources);
        Assert.Contains(
            sources,
            s => s.GetProperty("sourceName").GetString() == SourceName
                && s.GetProperty("lastOutcome").GetString() == "upstream-error");

        // THE PROPERTY, asserted PER SOURCE ROW (CLAUDE.md §4: "some row has it" still passes an
        // implementation that writes one value to all of them). Every row publishes a closed
        // outcome and no free text at all.
        foreach (var source in sources)
        {
            var raw = source.GetRawText();
            Assert.False(
                source.TryGetProperty("lastError", out _),
                $"A source row still carries lastError: {raw}");
            Assert.Contains(
                source.GetProperty("lastOutcome").GetString(),
                ClosedOutcomeNames);
            Assert.DoesNotContain("missing unit in duration", raw, StringComparison.Ordinal);
            Assert.DoesNotContain(PlantedHost, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(PlantedKey, raw, StringComparison.Ordinal);
        }

        // And the worker block, which publishes the same closed vocabulary and no text.
        var worker = status.RootElement.GetProperty("worker");
        Assert.False(
            worker.TryGetProperty("lastError", out _),
            $"The worker block still carries lastError: {worker.GetRawText()}");
        Assert.Contains(worker.GetProperty("lastOutcome").GetString(), ClosedOutcomeNames);

        // Whole-body sweep, belt and braces over the per-row assertions above.
        Assert.DoesNotContain("missing unit in duration", body, StringComparison.Ordinal);
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
    /// The closed vocabulary <c>StatusEndpoint.ToOutcomeLabel</c> may publish. Spelled out here as
    /// literals rather than derived from the enum on purpose: deriving them would make this test
    /// agree with the implementation automatically, including about a member added later that was
    /// never meant to be public. The wire format is closed by construction, and this is the list.
    /// </summary>
    private static readonly string[] ClosedOutcomeNames =
    [
        "none",
        "upstream-error",
        "unreachable",
        "timeout",
        "auth-rejected",
        "internal-error",
        "unknown",
    ];

    /// <summary>
    /// <b>arb-fbx: the proven leak body, end to end on the real endpoint.</b> This exact excerpt
    /// passed the test above unchanged while publishing BOTH of its hostnames to the unauthenticated
    /// dashboard — a two-label private-suffix name and a single-label one, neither of which any
    /// pattern in the original scrubber matched. The unit test pins the scrubber; this pins that the
    /// clean result is what <c>/api/status</c> actually serves.
    ///
    /// <para>Positive controls as everywhere here: both names are shown findable in the input, the
    /// response is shown to carry the real reason, and the redaction token is asserted present.</para>
    /// </summary>
    [Fact]
    public async Task The_reported_two_hostname_leak_body_reaches_status_with_neither_name()
    {
        const string dottedHost = "ollama.lan";
        const string bareHost = "ollama-gpu-rig";
        const string breakerSourceName = "OllamaTwoHostnameLeak";
        var leakyBody =
            $$"""{"error":"upstream {{dottedHost}} refused via {{bareHost}}: time: missing unit in duration \"-1\""}""";

        Assert.Contains(dottedHost, leakyBody, StringComparison.Ordinal);
        Assert.Contains(bareHost, leakyBody, StringComparison.Ordinal);

        var exception = new OllamaRequestException(System.Net.HttpStatusCode.BadRequest, leakyBody);

        using (var scope = _factory.Services.CreateScope())
        {
            var breaker = scope.ServiceProvider.GetRequiredService<IAsyncCircuitBreaker>();
            await breaker.RecordFailureAsync(breakerSourceName, exception);
        }

        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        // POSITIVE CONTROL FIRST: the error arrived and the useful reason survived the scrubbing,
        // read off the admin body where arb-mhd2 moved it.
        var adminBody = await GetDiagnosticsBodyAsync(client);
        Assert.Contains("missing unit in duration", adminBody, StringComparison.Ordinal);
        Assert.Contains(
            Arbitarr.Core.Diagnostics.SanitizedErrorDescription.Replacement,
            adminBody,
            StringComparison.Ordinal);

        // THE PROPERTY: both hostnames are gone, not just the one a single-shape fix would catch.
        Assert.DoesNotContain(dottedHost, adminBody, StringComparison.Ordinal);
        Assert.DoesNotContain(bareHost, adminBody, StringComparison.Ordinal);

        // And the public body carries neither the names nor the excerpt that contained them.
        var body = await client.GetStringAsync("/api/status");
        Assert.DoesNotContain("missing unit in duration", body, StringComparison.Ordinal);
        Assert.DoesNotContain(dottedHost, body, StringComparison.Ordinal);
        Assert.DoesNotContain(bareHost, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-mhd2: the OTHER writer, end to end on the real routes. The two published fields changed
    /// together, so both are driven here: the breaker path above, and the WORKER's faulted refresh
    /// cycle here. A change that gated the source detail while leaving the worker's free text on the
    /// unauthenticated body would pass every assertion above and fail this one.
    ///
    /// <para>The real singleton is resolved from the composed host and faulted with a real
    /// exception, so the sanitize-and-classify step under test is the one Program.cs wired — not a
    /// stub standing in for it.</para>
    /// </summary>
    [Fact]
    public async Task A_faulted_refresh_cycle_reaches_status_as_an_outcome_with_its_detail_admin_gated()
    {
        const string WorkerMarker = "WorkerCycleMarkerZzq47";
        // A documentation host, never a real one: this file is public.
        var faultMessage = $"No such host is known. (refresh.example.invalid:5076) {WorkerMarker}";
        var cycleFault = new HttpRequestException(faultMessage, inner: null, HttpStatusCode.BadGateway);

        // DETECTABILITY CONTROL: the marker really is in what the worker will be handed.
        Assert.Contains(WorkerMarker, cycleFault.Message, StringComparison.Ordinal);

        await SeedAdminKeyAsync();

        // The REAL health sink Program.cs registered and the refresh worker writes to — a singleton,
        // so what is recorded here is exactly what both endpoints read.
        var health = _factory.Services.GetRequiredService<IRefreshWorkerHealth>();
        health.CycleFaulted(
            DateTimeOffset.UtcNow,
            Arbitarr.Core.Diagnostics.SanitizedErrorDescription.Describe(cycleFault),
            Arbitarr.Core.Diagnostics.SourceStatusOutcomeClassifier.Classify(cycleFault));

        using var client = _factory.CreateClient();

        // POSITIVE CONTROL FIRST: the worker's detail IS on the admin read, so its absence from the
        // public body below is the gating working rather than a cycle that never faulted.
        var adminBody = await GetDiagnosticsBodyAsync(client);
        Assert.Contains("HttpRequestException", adminBody, StringComparison.Ordinal);
        Assert.Contains("502", adminBody, StringComparison.Ordinal);

        // The marker itself never survives sanitization, so the control above is the type-and-status
        // description. This asserts the sanitizer still did its job on the ADMIN surface too.
        Assert.DoesNotContain(WorkerMarker, adminBody, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh.example.invalid", adminBody, StringComparison.Ordinal);

        var body = await client.GetStringAsync("/api/status");

        using var status = System.Text.Json.JsonDocument.Parse(body);
        var worker = status.RootElement.GetProperty("worker");

        // NON-VACUITY: the worker block reports the failure, as a closed outcome.
        Assert.Equal("upstream-error", worker.GetProperty("lastOutcome").GetString());

        // THE PROPERTY: no free text on the worker block at all.
        Assert.False(
            worker.TryGetProperty("lastError", out _),
            $"The worker block still carries lastError: {worker.GetRawText()}");
        Assert.DoesNotContain(WorkerMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh.example.invalid", body, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", body, StringComparison.Ordinal);
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
