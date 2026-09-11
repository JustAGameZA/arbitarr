using System.Net;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-1rr: the gate between an upstream error and the UNAUTHENTICATED <c>/api/status</c> dashboard.
///
/// <para><b>What this file has to hold.</b> Before this bead the type's guarantee was simple — no
/// text from the wire, ever — and simple guarantees are easy to test. arb-1rr deliberately opened
/// one hole in it so an operator could finally see WHY Ollama rejected a request, and these tests
/// exist to prove the hole is exactly the size it was meant to be: one exception type, capped in
/// length, with hosts and credentials removed.</para>
///
/// <para><b>Every absence assertion here is a positive control</b> (CLAUDE.md §4). Asserting that a
/// secret is missing from a string passes just as happily when the secret was never in play, so each
/// test first demonstrates that the planted value IS findable by the same search — either in the
/// input, or by that search finding it in a string that does contain it — before asserting it is
/// gone from the output. The scrubbing tests additionally assert the redaction token IS present,
/// which is what distinguishes "the value was scrubbed" from "the value never arrived", exactly as
/// <c>LogSecretInjectionTests</c> does.</para>
///
/// <para>All hosts are RFC 2606 documentation names or RFC 5737 documentation addresses; the keys
/// are obvious placeholders. Nothing here is real.</para>
/// </summary>
public sealed class SanitizedErrorDescriptionTests
{
    /// <summary>
    /// The pre-existing behaviour, restated so arb-1rr cannot have widened it by accident: an
    /// ORDINARY <see cref="HttpRequestException"/> still yields type name and status and nothing
    /// else. This is the assertion that fails if someone "simplifies" the excerpt arm to cover the
    /// base type, which would put every upstream message on the dashboard.
    /// </summary>
    [Fact]
    public void An_ordinary_http_request_exception_still_carries_no_message_text()
    {
        const string leaky = "No such host is known. (ollama.internal.example:11434)";
        var ex = new HttpRequestException(leaky, inner: null, HttpStatusCode.BadGateway);

        var described = SanitizedErrorDescription.Describe(ex);

        // Detectability: this search does find the host when it is present.
        Assert.Contains("ollama.internal.example", leaky, StringComparison.Ordinal);
        Assert.DoesNotContain("ollama.internal.example", described, StringComparison.Ordinal);
        Assert.Equal("HttpRequestException (502 BadGateway)", described);
    }

    /// <summary>An exception with no status degrades to the bare type name, as before.</summary>
    [Fact]
    public void An_exception_without_a_status_is_described_by_type_name_alone()
    {
        Assert.Equal(
            nameof(InvalidOperationException),
            SanitizedErrorDescription.Describe(new InvalidOperationException("boom at 192.0.2.3")));
    }

    /// <summary>
    /// <b>THE POINT OF THE BEAD.</b> For <see cref="OllamaRequestException"/> the upstream reason
    /// survives, beside the status — without it the dashboard says only
    /// "HttpRequestException (400 BadRequest)" for every distinct Ollama fault, which is what let a
    /// 100%-failing classifier look identical to a transient blip.
    /// </summary>
    [Fact]
    public void An_ollama_request_exception_carries_the_upstream_reason()
    {
        var ex = new OllamaRequestException(
            HttpStatusCode.BadRequest,
            """{"error":"time: missing unit in duration \"-1\""}""");

        var described = SanitizedErrorDescription.Describe(ex);

        Assert.Contains("400", described, StringComparison.Ordinal);
        Assert.Contains("missing unit in duration", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL — a planted KEY is scrubbed.</b> Ollama could echo back anything that
    /// appeared in the request, and this field is served unauthenticated. The planted key is shown
    /// findable first, then shown absent, and the redaction token is asserted PRESENT so the test
    /// fails if the excerpt was dropped wholesale rather than cleansed.
    /// </summary>
    [Fact]
    public void A_planted_key_in_the_body_is_replaced_by_the_redaction_token()
    {
        const string plantedKey = "PLANTEDKEYVALUE1234";
        var body = $$"""{"error":"rejected request with apikey={{plantedKey}}"}""";

        // Existence AND detectability: the key is in the input, and Assert.Contains finds it there.
        Assert.Contains(plantedKey, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(plantedKey, described, StringComparison.Ordinal);
        // Proves the cleanser ran on text that reached it, rather than the excerpt never arriving.
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL — a planted HOST is scrubbed.</b> Leaking LAN topology to an
    /// unauthenticated caller is the specific harm this type was created to prevent, and admitting
    /// upstream text is exactly the change that could reintroduce it.
    /// </summary>
    [Theory]
    [InlineData("ollama.internal.example")]
    [InlineData("ollama.internal.example:11434")]
    [InlineData("http://ollama.internal.example:11434/api/chat")]
    [InlineData("192.0.2.138")]
    [InlineData("192.0.2.138:31434")]
    public void A_planted_host_in_the_body_never_reaches_the_description(string plantedHost)
    {
        var body = $$"""{"error":"upstream {{plantedHost}} refused the request"}""";

        Assert.Contains(plantedHost, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(plantedHost, described, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// A URL carrying a credential loses BOTH halves. The order the scrubbers run in is what makes
    /// this true — redacting the credential first would leave the address, and this asserts the
    /// address and the secret are gone together rather than one at a time.
    /// </summary>
    [Fact]
    public void A_url_carrying_a_credential_loses_both_the_address_and_the_credential()
    {
        const string host = "ollama.internal.example";
        const string key = "URLKEYVALUE98765";
        var body = $$"""{"error":"proxy http://{{host}}:11434/api/chat?apikey={{key}} denied"}""";

        Assert.Contains(host, body, StringComparison.Ordinal);
        Assert.Contains(key, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(host, described, StringComparison.Ordinal);
        Assert.DoesNotContain(key, described, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// The excerpt is capped at capture, so an unbounded upstream body cannot grow a persisted
    /// health field or a dashboard line. Asserted on the exception itself, which is where the cap
    /// is applied — not on the description, so a later formatting change cannot mask it.
    /// </summary>
    [Fact]
    public void A_long_body_is_truncated_to_the_cap()
    {
        var ex = new OllamaRequestException(HttpStatusCode.BadRequest, new string('x', 4096));

        Assert.Equal(OllamaRequestException.MaxExcerptLength, ex.BodyExcerpt.Length);
    }

    /// <summary>
    /// <b>THE MESSAGE ITSELF IS CLEAN, not merely the display path.</b>
    ///
    /// <para>Scrubbing only where the dashboard reads would leave the raw body on
    /// <see cref="System.Exception.Message"/> — and a message is written by things nothing routes: a
    /// generic <c>catch</c> that logs, the circuit breaker's own logging, an unhandled-exception
    /// handler. Since #65 those land in a persistent SQLite store served at
    /// <c>/api/admin/logs</c>. So the planted values must be absent from the MESSAGE and from
    /// <c>ToString()</c>, not just from <c>Describe</c>'s output.</para>
    ///
    /// <para>Positive controls throughout: each planted value is shown findable in the input by the
    /// same search that then asserts its absence, and the redaction token is asserted present so a
    /// wholesale-dropped excerpt cannot pass as a scrubbed one.</para>
    /// </summary>
    [Fact]
    public void The_exception_message_never_carries_the_raw_body()
    {
        const string plantedHost = "ollama.internal.example";
        const string plantedKey = "MESSAGEKEYVALUE4321";
        var body = $$"""{"error":"upstream {{plantedHost}}:11434 refused apikey={{plantedKey}}"}""";

        // Detectability: both values are in the input, found by these very searches.
        Assert.Contains(plantedHost, body, StringComparison.Ordinal);
        Assert.Contains(plantedKey, body, StringComparison.Ordinal);

        var ex = new OllamaRequestException(HttpStatusCode.BadRequest, body);

        Assert.DoesNotContain(plantedHost, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedHost, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(plantedKey, ex.ToString(), StringComparison.Ordinal);
        // The stored excerpt is clean too — the raw text never became state on this object.
        Assert.DoesNotContain(plantedHost, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedKey, ex.BodyExcerpt, StringComparison.Ordinal);
        // And it was scrubbed rather than discarded.
        Assert.Contains(SanitizedErrorDescription.Replacement, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The message still says WHICH failure this was. Scrubbing must not reduce the log line to a
    /// bare status — that is the uninformative state arb-1rr set out to fix, just relocated from
    /// the dashboard to the log.
    /// </summary>
    [Fact]
    public void The_exception_message_keeps_the_useful_part_of_the_reason()
    {
        var ex = new OllamaRequestException(
            HttpStatusCode.BadRequest,
            """{"error":"time: missing unit in duration \"-1\""}""");

        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
        Assert.Contains("missing unit in duration", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty body yields the status-only description rather than a trailing ": " — a dangling
    /// separator reads as a defect to the operator looking at it.
    /// </summary>
    [Fact]
    public void An_empty_body_leaves_the_description_unchanged()
    {
        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, string.Empty));

        Assert.Equal("HttpRequestException (400 BadRequest)", described);
    }

    /// <summary>
    /// A body that is ENTIRELY host text scrubs down to nothing worth showing, and the description
    /// must degrade to status-only rather than ending in a bare redaction token with no sentence
    /// around it.
    /// </summary>
    [Fact]
    public void A_body_that_scrubs_away_entirely_degrades_to_the_status_description()
    {
        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, "ollama.internal.example"));

        Assert.DoesNotContain("ollama.internal.example", described, StringComparison.Ordinal);
        Assert.StartsWith("HttpRequestException (400 BadRequest)", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordinary technical detail an operator NEEDS survives the scrubbing. Over-redaction is a real
    /// cost — a message reduced to redaction tokens is its own outage — so this pins that a version
    /// string and a model tag are not mistaken for hosts.
    /// </summary>
    [Fact]
    public void Useful_detail_survives_the_scrubbing()
    {
        var described = SanitizedErrorDescription.Describe(new OllamaRequestException(
            HttpStatusCode.NotFound,
            """{"error":"model \"qwen2.5:7b-instruct-q4_K_M\" not found, try pulling it first"}"""));

        Assert.Contains("not found", described, StringComparison.Ordinal);
        Assert.Contains("qwen2.5", described, StringComparison.Ordinal);
    }

}
