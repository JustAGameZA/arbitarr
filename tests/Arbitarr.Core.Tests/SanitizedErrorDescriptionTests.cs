using System.Net;
using System.Text.RegularExpressions;
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
    /// arb-959: <b>an address that STRADDLES the excerpt cut must not leave a fragment behind.</b>
    ///
    /// <para>The old order cut the collapsed body to <see cref="OllamaRequestException.MaxExcerptLength"/>
    /// and scrubbed the result. A host positioned across offset 200 was therefore handed to the
    /// scrubber already cut in half, and half a host is not host-shaped: <c>ollama.int</c> carries
    /// one dot, no port and no recognised pseudo-TLD, so <see cref="SanitizedErrorDescription"/>'s
    /// <c>HostWithPort</c> and <c>DottedHostName</c> arms both pass over it and the leading half of a
    /// real internal hostname published on an unauthenticated surface. Scrubbing before the cut is
    /// what closes it, and this test is the reason that order cannot be "tidied" back.</para>
    ///
    /// <para>Positive control, in the strong form CLAUDE.md §4 asks for: the assertion is not merely
    /// that the whole host is absent (a wholesale-dropped excerpt would satisfy that), but that the
    /// specific FRAGMENT the old order stranded is absent, AND that the redaction token is present —
    /// proving the scrubber saw the address and removed it rather than never reaching it. The
    /// fragment is first shown findable in the old order's own output, computed here, so the search
    /// that then asserts absence is demonstrably capable of finding it.</para>
    /// </summary>
    [Fact]
    public void A_host_straddling_the_excerpt_cut_leaves_no_fragment()
    {
        // RFC 2606 reserved name, never a real host.
        const string plantedHost = "ollama.internal.example:11434";
        const string prefix = """{"error":"dial tcp """;
        // Positions the host at offset 190, so the cut at 200 falls INSIDE it — leaving "ollama.int",
        // which has one dot, no port and no recognised pseudo-TLD, so NO arm matches it.
        var body = prefix + new string('x', 190 - prefix.Length) + plantedHost + " connect: refused\"}";

        // The old order, reproduced through the public surface. Cut to the cap FIRST (the body has
        // no whitespace runs, so collapsing is a no-op and this slice is exactly what the old
        // Excerpt() produced), then hand the already-cut text through the constructor — whose own
        // bounds are no-ops at this length — so all that remains is the scrub, applied to cut text.
        const string strandedFragment = "ollama.int";
        var oldOrderExcerpt =
            new OllamaRequestException(
                HttpStatusCode.BadRequest,
                body[..OllamaRequestException.MaxExcerptLength]).BodyExcerpt;
        // Detectability: the old order really did strand a fragment of the planted host, and this
        // is the very search used to assert its absence below.
        Assert.Contains(strandedFragment, oldOrderExcerpt, StringComparison.Ordinal);

        var ex = new OllamaRequestException(HttpStatusCode.BadRequest, body);

        Assert.DoesNotContain(strandedFragment, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedHost, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(strandedFragment, ex.Message, StringComparison.Ordinal);
        // And it was scrubbed, not merely cut short of the address.
        Assert.Contains(SanitizedErrorDescription.Replacement, ex.BodyExcerpt, StringComparison.Ordinal);
        // The ceiling still holds.
        Assert.True(ex.BodyExcerpt.Length <= OllamaRequestException.MaxExcerptLength);
    }

    /// <summary>
    /// arb-959: the SECOND bound — <see cref="OllamaRequestException.MaxScrubInputLength"/> — has a
    /// straddle of its own, and this test documents the accepted behaviour rather than pretending it
    /// away.
    ///
    /// <para>A body long enough to be cut at the scrub-input bound can have a host across THAT cut,
    /// and the resulting fragment is no more address-shaped than before. The difference is where it
    /// sits: the input bound is four times the excerpt cap, so anything stranded there is hundreds of
    /// characters past the end of the excerpt and is discarded by the final truncation. Nothing from
    /// that region can reach a dashboard or a log line. That is why one bound may be generous and the
    /// other exact.</para>
    ///
    /// <para>Positive control: the host is shown present in the input by the same search, and an
    /// address planted INSIDE the excerpt region of the same body is shown to have been scrubbed —
    /// so a vacuously empty excerpt cannot pass this test.</para>
    /// </summary>
    [Fact]
    public void A_host_straddling_the_scrub_input_bound_never_reaches_the_excerpt()
    {
        const string farHost = "far.internal.example:11434";
        const string nearHost = "near.internal.example:11434";
        const string prefix = """{"error":"dial tcp """;
        var bound = OllamaRequestException.MaxScrubInputLength;

        // nearHost sits early (inside the excerpt region); farHost straddles the input bound.
        var head = prefix + nearHost + " then ";
        var body = head + new string('x', bound - 10 - head.Length) + farHost + " refused\"}";

        // Detectability: both planted hosts are in the input, found by these very searches.
        Assert.Contains(farHost, body, StringComparison.Ordinal);
        Assert.Contains(nearHost, body, StringComparison.Ordinal);

        var ex = new OllamaRequestException(HttpStatusCode.BadRequest, body);

        // The far host's region is past the excerpt entirely — neither it nor any prefix of it lands.
        Assert.DoesNotContain(farHost, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("far.int", ex.BodyExcerpt, StringComparison.Ordinal);
        // The near host was inside the excerpt region and was scrubbed there.
        Assert.DoesNotContain(nearHost, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.True(ex.BodyExcerpt.Length <= OllamaRequestException.MaxExcerptLength);
    }

    /// <summary>
    /// arb-959/arb-nid6: <b>the shifted-straddle case — a fragment that sits comfortably PAST the
    /// excerpt cut in the raw body can still straddle that cut once redaction runs.</b>
    ///
    /// <para>Scrubbing runs before the cut, and redaction SHRINKS the text: each host it removes is
    /// replaced by a shorter token. A fragment placed at raw offset ~230 — 30 characters past the
    /// 200-character excerpt cap, comfortably clear of it before anything is scrubbed — is not safe
    /// from the cap once enough redactions happen ahead of it. This body plants many single-label
    /// hosts (a Docker/Compose-style shape) early in the text; each one collapses to the shared
    /// replacement token, which is shorter than the host it replaces, so the text ahead of the
    /// planted fragment shrinks with every one of them. Enough such redactions ahead of it pull a
    /// fragment planted past raw offset 200 back across the (post-scrub) 200-character cut, landing
    /// it half in and half out of the excerpt — exactly the straddle shape
    /// <see cref="A_host_straddling_the_excerpt_cut_leaves_no_fragment"/> closes for a fragment that
    /// starts out AT the cut. This test pins that the same closure holds for a fragment that starts
    /// out safely past it, because the type's real guarantee was never "nothing at raw offset 200
    /// leaks" — it is that nothing scrubbed is ever handed to the excerpt half-redacted, whatever raw
    /// offset it originally sat at, provided it began within
    /// <see cref="OllamaRequestException.MaxScrubInputLength"/>.</para>
    ///
    /// <para>Positive control: the planted fragment's host is shown present, whole, in the raw body
    /// before scrubbing (so the shift claim is not vacuous), and the redaction token is asserted
    /// present in the output so a wholesale-empty excerpt cannot pass as evidence of a fix.</para>
    /// </summary>
    [Fact]
    public void A_fragment_planted_past_the_excerpt_cut_still_straddles_it_once_redaction_shifts_it()
    {
        // A short, single-label host shape the ContextualSingleLabelHost arm redacts, shorter after
        // redaction than before — this is what makes the text SHRINK ahead of the planted fragment as
        // more of these are scrubbed.
        const string shrinkingHost = "dial gpu_box_example failed; ";
        const string plantedHost = "ollama.internal.example:11434";
        const string prefix = """{"error":""";

        // Repeat the shrinking host enough times to land the planted host at a raw offset safely past
        // MaxExcerptLength (200) before any scrubbing, while staying well inside MaxScrubInputLength.
        var head = prefix;
        while (head.Length < 230 - prefix.Length)
        {
            head += shrinkingHost;
        }

        const string strandedFragment = "ollama.int";
        var body = head + plantedHost + " connect: refused\"}";

        // Detectability: the planted host really does start past the raw excerpt cut, and is present
        // whole in the input — the shift only happens once scrubbing runs.
        Assert.True(head.Length > OllamaRequestException.MaxExcerptLength);
        Assert.Contains(plantedHost, body, StringComparison.Ordinal);

        var ex = new OllamaRequestException(HttpStatusCode.BadRequest, body);

        Assert.DoesNotContain(strandedFragment, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(plantedHost, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.DoesNotContain(strandedFragment, ex.Message, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, ex.BodyExcerpt, StringComparison.Ordinal);
        Assert.True(ex.BodyExcerpt.Length <= OllamaRequestException.MaxExcerptLength);
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

    /// <summary>
    /// <b>arb-fbx — the host shapes the original patterns walked straight past.</b> Each row is a
    /// real leak found by the #193 security review: a single-label LAN host (no dot, no port, so no
    /// structural pattern matched it), a two-label private-suffix name (the two-dot minimum spared
    /// it), an IPv6 literal in both the bracketed form Go's <c>net</c> prints and the bare one, and a
    /// percent-encoded URL (no literal <c>://</c> left for the URL pattern to find).
    ///
    /// <para>Positive control per row, as everywhere in this file: the planted value is shown
    /// findable in the input by the same search that then asserts its absence, and the redaction
    /// token is asserted PRESENT so a wholesale-dropped excerpt cannot pass as a scrubbed one.</para>
    /// </summary>
    [Theory]
    // Single-label host named by a connection verb — no dot, no port.
    [InlineData("upstream ollama-gpu-rig rejected the request", "ollama-gpu-rig")]
    [InlineData("dial mediabox failed", "mediabox")]
    [InlineData("refused via ollama-gpu-rig", "ollama-gpu-rig")]
    // Two-label names on private-network suffixes.
    [InlineData("cannot reach ollama.lan right now", "ollama.lan")]
    [InlineData("cannot reach nas.local right now", "nas.local")]
    [InlineData("cannot reach box.home right now", "box.home")]
    // IPv6, bracketed and bare, with and without a port. RFC 3849 documentation prefix.
    //
    // The planted value asserted here is a FRAGMENT of the address, not the whole literal, and that
    // is deliberate: without the IPv6 arm, HostWithPort eats "db8:1234" out of the middle and
    // publishes "[2001:<redacted>::42]:11434". A whole-literal assertion passes against that — the
    // literal really is absent — while the address is on the dashboard. Asserting a surviving
    // fragment is what makes the row bite; the mutation run in the PR body shows it failing.
    [InlineData("dial tcp [2001:db8:1234::42]:11434: connect refused", "::42")]
    [InlineData("dial tcp [2001:db8:1234::42]: connect refused", "::42")]
    [InlineData("peer 2001:db8:1234::42 went away", "::42")]
    // #195 review: a bare address FOLLOWED BY A PORT. The hex-group cap stops at four characters,
    // so the port used to be split and a digit left beside the token — the planted value is the
    // whole port for that reason.
    [InlineData("dial tcp fd00:1234:5678::42:11434: connect refused", "11434")]
    // #195 review: %zone matched neither form, so the whole address published. Planted on the zone
    // id, which nothing else in the pipeline can redact.
    [InlineData("peer fe80::1%eth0 went away", "eth0")]
    [InlineData("dial tcp [fe80::1%eth0]:11434 refused", "eth0")]
    // #195 review: TWO-GROUP compressed addresses escaped the bare form entirely — fd00::42 is the
    // ULA this file's own comments cite.
    //
    // The planted value is the SUFFIX, not the whole literal, for the same reason the bracketed rows
    // above plant "::42": against the vulnerable pattern the contextual arm still eats the leading
    // label, publishing "<redacted>::1" — which does not contain "fe80::1", so a whole-literal
    // assertion passes while the address is on the dashboard. Verified by mutation; see the PR.
    [InlineData("peer fe80::1 went away", "::1")]
    [InlineData("peer fd00::42 went away", "::42")]
    [InlineData("peer fe80::abcd went away", "::abcd")]
    // Percent-encoded URL: no literal "://" for the URL pattern to anchor on.
    [InlineData("proxy http%3A%2F%2Follama.internal.example%3A11434%2Fapi%2Fchat denied", "ollama.internal.example")]
    // Bare credential with no separator and no scheme.
    [InlineData("invalid key sk-live-PLACEHOLDER9f8e7d6c", "sk-live-PLACEHOLDER9f8e7d6c")]
    public void A_host_or_credential_shape_the_original_patterns_missed_is_now_redacted(
        string excerptText,
        string plantedValue)
    {
        var body = $$"""{"error":"{{excerptText}}"}""";

        // Detectability: the planted value is genuinely in the input, found by this very search.
        Assert.Contains(plantedValue, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(plantedValue, described, StringComparison.Ordinal);
        // Proves the scrubber fired on text that reached it, rather than the excerpt never arriving.
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The proven leak body from the arb-fbx report, end to end.</b> It carries two hostnames of
    /// different shapes in one sentence — a two-label private-suffix name and a single-label one —
    /// and passed the pre-arb-fbx tests unchanged with both published. Kept as its own test rather
    /// than another row because the property is that BOTH go at once: a fix for either shape alone
    /// would still pass a single-value row.
    /// </summary>
    [Fact]
    public void The_reported_leak_body_publishes_neither_of_its_two_hostnames()
    {
        const string dottedHost = "ollama.lan";
        const string bareHost = "ollama-gpu-rig";
        var body =
            $$"""{"error":"upstream {{dottedHost}} refused via {{bareHost}}: time: missing unit in duration \"-1\""}""";

        Assert.Contains(dottedHost, body, StringComparison.Ordinal);
        Assert.Contains(bareHost, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(dottedHost, described, StringComparison.Ordinal);
        Assert.DoesNotContain(bareHost, described, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
        // Over-scrubbing is the smaller failure, but it is still a failure: the useful reason — the
        // only thing that tells one Ollama 400 from another — must survive alongside the redactions.
        Assert.Contains("missing unit in duration", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Detail an operator needs must NOT be redacted by the widened patterns.</b> Each row is a
    /// shape a plausible-but-wrong widening would have eaten: a version string (why
    /// <c>DottedHostName</c> keeps its alphabetic-final-label guard), the sentence-final "duration."
    /// (why it keeps a label minimum), a model tag whose colon and dot make it look like both a
    /// host:port and a dotted name, and a bare status code.
    ///
    /// <para>These rows are the price side of the trade this file states — over-scrubbing is the
    /// smaller failure, not a free one — and they are what fails if the single-label arm is ever
    /// widened to generic words like "model" or "error".</para>
    /// </summary>
    [Theory]
    [InlineData("upgrade to 1.2.3 or later", "1.2.3")]
    [InlineData("time: missing unit in duration \\\"-1\\\"", "missing unit in duration")]
    [InlineData("model llama3.1:8b not found", "llama3.1:8b")]
    [InlineData("model \\\"phi4:14b\\\" not found", "phi4:14b")]
    [InlineData("rejected with HTTP 400", "HTTP 400")]
    // arb-qj9 rows: one per arm widened by that bead, so each widening carries its own price check.
    [InlineData("model qwen2.5 not found", "qwen2.5")]                       // .5 is not a suffix
    [InlineData("missing config.json in /etc", "config.json")]               // .json is not a suffix
    [InlineData("invalid option: num_ctx", "num_ctx")]                       // underscore is not a host
    [InlineData("context length 4096 exceeded", "context length 4096")]
    [InlineData("retry 3 of 5 after 250 ms", "retry 3 of 5")]
    [InlineData("unsupported format: application/json", "application/json")] // a / path is not UNC
    [InlineData("post_processing failed for job_id 4417", "post_processing")]
    public void Useful_detail_is_not_eaten_by_the_widened_patterns(
        string excerptText,
        string mustSurvive)
    {
        var body = $$"""{"error":"{{excerptText}}"}""";

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.Contains(mustSurvive, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>What this DELIBERATELY over-scrubs, recorded so it is a decision rather than a surprise.</b>
    /// A bare colon-separated run of hex-like groups cannot be distinguished from an IPv6 address by
    /// shape alone, so a MAC address and a clock time are redacted with it. Both are acceptable
    /// losses under this file's stated principle — an over-scrubbed error message is a smaller
    /// failure than a published internal address — and both are cheap to recognise in a log, where
    /// the operator still has the unredacted text.
    ///
    /// <para>This test exists so that widening the IPv6 arm further, or narrowing it to win these
    /// back, is a visible change to a recorded decision rather than an unnoticed side effect.</para>
    /// </summary>
    [Theory]
    [InlineData("checksum aa:bb:cc ok", "aa:bb:cc")]
    [InlineData("at 12:34:56 the job ran", "12:34:56")]
    public void Hex_shaped_runs_are_over_scrubbed_on_purpose(string excerptText, string overScrubbed)
    {
        var body = $$"""{"error":"{{excerptText}}"}""";

        Assert.Contains(overScrubbed, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(overScrubbed, described, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>arb-qj9: the shapes the #195 security review found still passing.</b> Each row is one
    /// shape, with the token that ACTUALLY SURVIVED the old scrubber as the planted value.
    ///
    /// <para><b>Why the planted token is a fragment and not the whole value</b> — the lesson arb-fbx
    /// paid for twice. Several of these were only PARTIALLY scrubbed before: the double-encoded URL
    /// already lost its dotted middle to <c>DottedHostName</c>, and the contextual arm eats the label
    /// in front of a bare name. Planting the whole value and asserting its absence would therefore
    /// have passed against a scrubber with no fix at all — a vacuous row wearing the shape of a real
    /// one (CLAUDE.md §4). Every row below plants the exact substring the OLD pattern left visible,
    /// verified by running the pre-fix scrubber over each input.</para>
    /// </summary>
    [Theory]
    // (1) Double-encoded URL. Survived: the stranded first label and the encoded port. The dotted
    // middle was already being eaten, which is precisely why the whole URL is the wrong plant.
    [InlineData("proxy error for http%253A%252F%252Follama.internal.example%253A11434%252Fapi", "%253A11434")]
    [InlineData("proxy error for http%253A%252F%252Follama.internal.example%253A11434%252Fapi", "%252F%252Follama")]
    // (2) Trigger-word punctuation: colon, quote, and a bare Host header with no port to catch it.
    [InlineData("upstream: ollama-gpu-rig refused", "ollama-gpu-rig")]
    [InlineData("upstream \\\"ollama-gpu-rig\\\" refused", "ollama-gpu-rig")]
    [InlineData("Host: ollama-gpu-rig", "ollama-gpu-rig")]
    // (3) Underscore hostname — a Docker Compose service name.
    [InlineData("dial tcp gpu_box_example:11434: refused", "gpu_box_example")]
    // (4) Two-label names on suffixes outside the old allowlist.
    [InlineData("cannot reach mediabox.box", "mediabox.box")]
    [InlineData("cannot reach nas.localhost", "nas.localhost")]
    [InlineData("cannot reach relay.onion", "relay.onion")]
    // (5) Credential value class: '.' and '+' split the run, stranding the high-entropy tail.
    [InlineData("invalid key sk.live+PLACEHOLDER.9f8e supplied", "PLACEHOLDER.9f8e")]
    // (7) UNC path: the server name carries no dot, no port and no scheme, so nothing saw it.
    [InlineData("read failed \\\\\\\\NASBOX\\\\media\\\\share", "NASBOX")]
    public void The_shapes_the_security_review_found_no_longer_publish(
        string excerptText,
        string plantedToken)
    {
        var body = $$"""{"error":"{{excerptText}}"}""";

        // POSITIVE CONTROL: the token really is in the excerpt being scrubbed, so the absence
        // assertion below is about the pattern working rather than about an empty set.
        Assert.Contains(plantedToken, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(plantedToken, described, StringComparison.Ordinal);
        // Detectability: something was redacted, so this cannot pass by the excerpt never arriving.
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-qj9 (#206 review nit): <c>dial tcp HOST</c> redacts the HOST, not the protocol.
    ///
    /// <para>The contextual arm consumed "tcp" as its value and stopped, publishing
    /// <c>dial &lt;redacted&gt; gpu_box_example</c> — the protocol name redacted and the actual host
    /// left standing, which is the exact inversion of the arm's purpose. This is the portless case:
    /// with a port, <c>HostWithPort</c> covers the host regardless, so the bug only bites where
    /// nothing else can catch it. Pinned as its own fact because the theory row above carries a port
    /// and would still pass with the mis-fire present.</para>
    /// </summary>
    [Fact]
    public void A_dial_error_redacts_the_host_and_keeps_the_protocol()
    {
        const string host = "gpu_box_example";
        var body = $$"""{"error":"dial tcp {{host}}: connect: connection refused"}""";

        Assert.Contains(host, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(host, described, StringComparison.Ordinal);
        // The protocol is diagnostic, not topology: redacting it was the bug.
        Assert.Contains("tcp", described, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);

        // IDEMPOTENCE, and the reason this assertion exists rather than being assumed: the first
        // attempt at the fix made the protocol-skip optional WITHOUT the (?!<) lookahead, which
        // passed every assertion above and then ate "tcp" on the second pass — the engine discarded
        // the optional group and fell back onto it once the host slot held "<redacted>". Describe()
        // scrubs an already-scrubbed body, so that second pass is the real path, and only this
        // assertion caught it.
        // Feeding the already-scrubbed EXCERPT back through as a body must scrub to itself. (The
        // excerpt, not `described` — re-describing would prepend a second exception header and the
        // comparison would fail for a reason that has nothing to do with the arms.)
        var excerpt = described[(described.IndexOf(": ", StringComparison.Ordinal) + 2)..];

        Assert.EndsWith(
            excerpt,
            SanitizedErrorDescription.Describe(
                new OllamaRequestException(HttpStatusCode.BadRequest, excerpt)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-qj9 (6): the email local part. Measured, and found ALREADY COVERED — the domain is
    /// removed by <c>DottedHostName</c>, leaving only "ops@". This test records that as the decided
    /// state rather than leaving the bead's claim to read as an open gap: the local part is a
    /// username, not LAN topology, and this file's remit is topology.
    ///
    /// <para>Positive control: the domain is shown present in the input and absent from the output,
    /// so this cannot pass against a scrubber that stopped removing domains.</para>
    /// </summary>
    [Fact]
    public void An_email_address_loses_its_domain_but_keeps_its_local_part()
    {
        const string domain = "internal.example";
        var body = $$"""{"error":"contact ops@{{domain}} for access"}""";

        Assert.Contains(domain, body, StringComparison.Ordinal);

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadRequest, body));

        Assert.DoesNotContain(domain, described, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
        Assert.Contains("ops@", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Truncation-before-scrub cannot split a URL so that a fragment survives.</b> The excerpt is
    /// cut at <see cref="OllamaRequestException.MaxExcerptLength"/> BEFORE it is scrubbed, so a URL
    /// can reach the scrubber chopped at an arbitrary offset. This sweeps every cut offset and
    /// requires that no offset leaves the host or the port behind.
    ///
    /// <para>The property holds because <c>Url()</c>'s tail is a greedy <c>\S+</c>, which swallows
    /// any prefix of a URL just as it swallows a whole one — and nothing else in the file pinned
    /// that, which is why this test exists. If someone replaces that tail with a structural host/
    /// port/path grammar, the truncated forms stop matching and this test fails rather than a
    /// hostname quietly reaching the dashboard.</para>
    ///
    /// <para><b>The claim is deliberately limited to URLs, and that limit is a real residual risk
    /// rather than an oversight (#195 review, HIGH).</b> The same sweep over a bracketed IPv6
    /// host:port and over a bare dotted name FAILS, and no regex can fix it: a cut at
    /// "dial tcp [fd00" or "cannot reach ollama" leaves text that is no longer host-SHAPED, so no
    /// shape-based pattern can recognise it. The contextual arm rescues only the cases where a
    /// trigger word sits directly in front of the fragment ("peer 2001" scrubs; "cannot reach
    /// ollama" does not, because "reach" is not a trigger and adding it would redact ordinary
    /// prose).</para>
    ///
    /// <para>What bounds the exposure is that truncation happens at ONE fixed offset
    /// (<see cref="OllamaRequestException.MaxExcerptLength"/>), not at an attacker-chosen one, so
    /// this leaks only when a host happens to straddle exactly that boundary, and then only a
    /// prefix of it. Closing it properly means scrubbing BEFORE truncating — a change to
    /// <c>OllamaRequestException</c>, outside this bead. Recorded here so the next reader inherits
    /// the limit rather than the false impression that all shapes are covered.</para>
    /// </summary>
    [Theory]
    [InlineData("http://ollama.internal.example:11434/api/chat", "ollama", "11434")]
    [InlineData("https://ollama.lan:11434/api/chat?apikey=PLACEHOLDER123", "ollama", "11434")]
    public void A_truncated_url_never_leaves_a_fragment_behind(
        string text,
        string hostFragment,
        string tailFragment)
    {
        for (var cut = 0; cut <= text.Length; cut++)
        {
            var prefix = text[..cut];

            // Detectability at the offsets where there is anything to find: the search used below
            // does locate the host in this very prefix when the cut left it intact.
            var hostIsPresent = prefix.Contains(hostFragment, StringComparison.Ordinal);

            // Driven through the public surface rather than the internal scrubber, so the test
            // exercises the path /api/status actually reads.
            var scrubbed = SanitizedErrorDescription.Describe(
                new OllamaRequestException(HttpStatusCode.BadRequest, prefix));

            Assert.False(
                scrubbed.Contains(hostFragment, StringComparison.Ordinal),
                $"Cut at {cut} published the host: '{scrubbed}' (host was in the input: {hostIsPresent})");
            Assert.False(
                scrubbed.Contains(tailFragment, StringComparison.Ordinal),
                $"Cut at {cut} published the tail fragment '{tailFragment}': '{scrubbed}'");
        }
    }

    /// <summary>
    /// arb-hihr, positive control: a body that would normally scrub to reveal a host must NEVER
    /// publish the unscrubbed (or partially-scrubbed) input when the regex pipeline times out. The
    /// nine local host/URL arms cannot have their compiled <c>matchTimeoutMilliseconds</c> swapped at
    /// runtime — it is baked into each arm's <c>GeneratedRegexAttribute</c> at compile time — so this
    /// forces <c>RegexMatchTimeoutException</c> deterministically via the <c>timeoutProbe</c>
    /// parameter on <see cref="SanitizedErrorDescription.Describe(Exception, Func{string, string}?)"/>
    /// rather than constructing an input that happens to exceed 250ms on the machine running the
    /// test; the #212 review measured only ~4.75ms for HostWithPort at 1596 characters (quadratic
    /// growth), and the excerpt this path actually receives is capped at
    /// <see cref="OllamaRequestException.MaxExcerptLength"/> (200 chars) before scrubbing ever runs,
    /// so 250ms is not reachable through the excerpt cap alone. A delegate parameter is used instead
    /// of a mutable static flag because <c>ProductionProcessGlobalStateTests</c> (arb-0hd0) requires
    /// every mutable static in a production assembly to be allow-listed as a process-global hazard,
    /// and a test-only toggle affecting every concurrent caller is exactly that hazard.
    /// </summary>
    [Fact]
    public void A_regex_timeout_degrades_to_the_placeholder_never_the_input()
    {
        const string hostFragment = "ollama.internal.example";
        var body = $"dial tcp {hostFragment}:11434: connection refused";

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadGateway, body),
            timeoutProbe: _ => throw new RegexMatchTimeoutException("arb-hihr test: forced timeout."));

        // Detectability: the search below does find the host in the untouched input.
        Assert.Contains(hostFragment, body, StringComparison.Ordinal);
        Assert.DoesNotContain(hostFragment, described, StringComparison.Ordinal);
        Assert.Contains("<redaction timed out>", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-hihr, control: with no <c>timeoutProbe</c> (the production default, <c>null</c>), ordinary
    /// scrubbing is unaffected by the added timeouts — the same host is still removed and the
    /// redaction token still appears, exactly as the pre-existing rows in this file assert for other
    /// inputs.
    /// </summary>
    [Fact]
    public void A_normal_input_still_scrubs_with_no_timeout_probe()
    {
        const string hostFragment = "ollama.internal.example";
        var body = $"dial tcp {hostFragment}:11434: connection refused";

        var described = SanitizedErrorDescription.Describe(
            new OllamaRequestException(HttpStatusCode.BadGateway, body));

        Assert.Contains(hostFragment, body, StringComparison.Ordinal);
        Assert.DoesNotContain(hostFragment, described, StringComparison.Ordinal);
        Assert.Contains(SanitizedErrorDescription.Replacement, described, StringComparison.Ordinal);
    }
}
