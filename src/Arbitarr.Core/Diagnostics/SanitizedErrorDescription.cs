using System.Text.RegularExpressions;
using Arbitarr.Core.Ai;

namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// Reduces an exception to a topology-safe description for any field that reaches an
/// unauthenticated surface: the exception type name, plus the HTTP status code when the exception
/// carries one, plus — for <see cref="OllamaRequestException"/> alone — a SCRUBBED excerpt of the
/// upstream response body.
/// </summary>
/// <remarks>
/// <see cref="System.Exception.Message"/> is never surfaced. For an
/// <see cref="System.Net.Http.HttpRequestException"/> raised by a DNS/connect failure or by
/// <c>EnsureSuccessStatusCode</c>, the message text routinely embeds the upstream host
/// (e.g. "No such host is known. (host:5076)"). Both <c>CircuitBreakerSnapshot.LastError</c> and
/// <c>RefreshWorkerHealthSnapshot.LastError</c> are served verbatim by the unauthenticated
/// <c>GET /api/status</c> dashboard, so every write to either must pass through here — a raw
/// <c>ex.Message</c> on those paths leaks LAN topology to any unauthenticated caller.
/// Full exception detail (message and stack) still reaches the operator through the logger.
///
/// <para><b>arb-1rr: the ONE exception to "no text from the wire", and why it is narrow.</b>
/// <see cref="OllamaRequestException.BodyExcerpt"/> is upstream text, which is exactly what the rule
/// above exists to keep off this surface. It is admitted only because the alternative was worse in
/// practice: every distinct Ollama 400 reported identically, leaving an operator with a red
/// dashboard and no way to tell a rejected option from an unknown model. The admission is bounded
/// three ways — by TYPE (only this exception; every other one still gets type name and status and
/// nothing else, so no existing path changes), by LENGTH (capped at capture, see
/// <see cref="OllamaRequestException.MaxExcerptLength"/>), and by CONTENT
/// (<see cref="ScrubForPublication"/> below).</para>
///
/// <para><b>The credential arms are ONE implementation, shared with the log cleanser (arb-6vf).</b>
/// They live in <see cref="CredentialPatterns"/> in this same project, and both this type and
/// <c>Arbitarr.Data.Logging.LogMessageCleanser</c> call it. Core is where the shared half has to
/// live: Data references Core, and Core referencing Data back would be a reference cycle that
/// breaks AC6 (<c>CoreIsolationTests</c> asserts Core references no other Arbitarr project).
/// Previously each sink held its own copy, kept equivalent by a comment asking the next contributor
/// to edit both — and they had already drifted, with the space-separated credential arm reaching
/// only this file. Add a credential pattern in <see cref="CredentialPatterns"/> and both sinks gain
/// it.</para>
///
/// <para><b>The HOST arms below stay here, and must not be shared.</b> They exist because this
/// output reaches an UNAUTHENTICATED caller. The log store behind <c>/api/admin/logs</c> is
/// admin-gated, and an operator diagnosing a connection failure needs the hostname that failed;
/// stripping it there would cost the diagnosis without protecting anyone who is not already
/// authenticated. Only credentials are common to both sinks.</para>
///
/// <para><b>Hosts are removed, not redacted in place.</b> Leaking LAN topology is the specific harm
/// this type was created to prevent, so anything host-shaped in the excerpt — a URL, a bare
/// host:port, a dotted name, an IP — is replaced rather than trusted. That is deliberately
/// aggressive: an over-scrubbed error message is a smaller failure than a published internal
/// hostname, and the unredacted body still reaches the operator through the log.</para>
/// </remarks>
public static partial class SanitizedErrorDescription
{
    /// <summary>
    /// The text substituted for a redacted value.
    ///
    /// <para>arb-6vf: aliases <see cref="CredentialPatterns.Replacement"/>, which
    /// <c>LogMessageCleanser.Replacement</c> also aliases, so the two sinks cannot drift apart on
    /// the token. Kept as a public const because call sites and tests reference it by this
    /// name.</para>
    /// </summary>
    public const string Replacement = CredentialPatterns.Replacement;

    /// <summary>
    /// arb-hihr: substituted for the WHOLE description when scrubbing could not complete within
    /// <see cref="CredentialPatterns.MatchTimeoutMilliseconds"/>. See the remarks on
    /// <see cref="ScrubForPublication"/> for why a fixed placeholder — never the unscrubbed excerpt,
    /// and never a partially-scrubbed one — is the only safe degrade on this unauthenticated path.
    ///
    /// <para><c>public</c>, like <see cref="Replacement"/> above, because call sites and tests
    /// reference it by name and there is no <c>InternalsVisibleTo</c> from this project to the test
    /// assembly (see <c>CredentialPatternsCrossSinkTests</c>'s remarks on why one was not added).</para>
    /// </summary>
    public const string TimeoutPlaceholder = "<redaction timed out>";

    /// <summary>
    /// arb-hihr: test-only seam forcing <c>RegexMatchTimeoutException</c> deterministically.
    /// The nine local arms cannot have their compiled <c>matchTimeoutMilliseconds</c> swapped at
    /// runtime — it is a compile-time <c>GeneratedRegexAttribute</c> argument — so a test cannot
    /// otherwise force a timeout without relying on an input that happens to exceed 250ms on the
    /// machine running the test. <c>false</c> (the production default) means scrub normally.
    ///
    /// <para><c>public</c> for the same reason as <see cref="TimeoutPlaceholder"/>: no
    /// <c>InternalsVisibleTo</c> reaches the test assembly. Test-only surface kept as narrow as
    /// possible — a single boolean, reset in a <c>finally</c> by every test that sets it.</para>
    /// </summary>
    public static bool ThrowTimeoutForTesting;

    /// <summary>Describes <paramref name="ex"/> without echoing its message text.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        // Must precede the HttpRequestException arm below: OllamaRequestException derives from it,
        // and a switch arm matches the FIRST pattern that fits. Reordering these silently drops the
        // excerpt and reduces this back to the status-only description arb-1rr set out to fix.
        OllamaRequestException { StatusCode: { } ollamaStatus } ollama =>
            AppendExcerpt(
                $"{nameof(System.Net.Http.HttpRequestException)} ({(int)ollamaStatus} {ollamaStatus})",
                ollama.BodyExcerpt),
        System.Net.Http.HttpRequestException { StatusCode: { } statusCode } =>
            $"{nameof(System.Net.Http.HttpRequestException)} ({(int)statusCode} {statusCode})",
        _ => ex.GetType().Name,
    };

    /// <summary>
    /// Appends the scrubbed excerpt to <paramref name="description"/>, or returns the description
    /// unchanged when scrubbing left nothing worth showing — an excerpt that was empty, or that was
    /// entirely redacted, adds no information and an empty ": " would only look like a defect.
    ///
    /// <para><b>This scrubs a value <see cref="OllamaRequestException"/> already scrubbed at
    /// construction, deliberately.</b> The pass is idempotent, so the cost is nothing, and the two
    /// layers guard different exits: that one keeps the raw body out of the LOG (an exception's
    /// message is written by handlers nobody routes), this one keeps it off the unauthenticated
    /// DASHBOARD. Removing this call because "it is already clean" would make the dashboard's
    /// safety depend on a property of a different type in a different file.</para>
    /// </summary>
    private static string AppendExcerpt(string description, string excerpt)
    {
        var scrubbed = ScrubForPublication(excerpt);

        return string.IsNullOrWhiteSpace(scrubbed)
            ? description
            : $"{description}: {scrubbed}";
    }

    /// <summary>
    /// Makes an upstream body excerpt safe for an unauthenticated surface: credentials redacted,
    /// anything host-shaped removed.
    ///
    /// <para>Order matters. URLs run FIRST, because a URL can carry a credential in its query string
    /// or path; redacting the credential first would leave the surrounding address behind, and
    /// removing the address first takes the credential with it. Bare host:port and dotted-name
    /// forms run after, to catch what was never part of a URL.</para>
    ///
    /// <para><b>arb-hihr: a <c>RegexMatchTimeoutException</c> anywhere in this pipeline
    /// degrades to <see cref="TimeoutPlaceholder"/> for the WHOLE description, never a
    /// partially-scrubbed string and never the unscrubbed <paramref name="excerpt"/>.</b> A partial
    /// result is unsafe here specifically because of this method's own ordering guarantee above: if
    /// (say) <see cref="HostWithPort"/> is the arm that times out, everything BEFORE it in the
    /// pipeline is scrubbed but <see cref="IpAddress"/>, <see cref="DottedHostName"/> and the
    /// credential arms never ran, so a host or credential shaped exactly for one of the later arms
    /// would publish untouched — worse than the ordinary "arm never even attempted" case this file
    /// already accepts for other reasons. Failing loud instead (letting the exception propagate) was
    /// rejected too: <c>Describe</c> is called from <c>SourceCircuitBreaker.DescribeSanitized</c> and
    /// <c>RefreshWorker</c>, neither of which catches this exception type, so an uncaught timeout
    /// would surface as an unhandled exception in a breaker/refresh path — turning a scrubbing edge
    /// case into an availability incident. A fixed placeholder costs the operator one specific
    /// diagnostic string on the rare pathological input; an unhandled exception costs the whole
    /// refresh or breaker cycle. Nothing is logged from Core for this — logging remains the
    /// caller's/handler's responsibility, per this type's existing contract that Core never writes to
    /// the logger itself.</para>
    /// </summary>
    internal static string ScrubForPublication(string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            return string.Empty;
        }

        try
        {
            return ScrubForPublicationCore(excerpt);
        }
        catch (RegexMatchTimeoutException)
        {
            return TimeoutPlaceholder;
        }
    }

    private static string ScrubForPublicationCore(string excerpt)
    {
        if (ThrowTimeoutForTesting)
        {
            throw new RegexMatchTimeoutException("arb-hihr test seam: forced timeout.");
        }

        var text = excerpt;

        text = Url().Replace(text, Replacement);

        // arb-qj9: the double-encoded form MUST precede the single-encoded one. "%252F" contains
        // "%25" followed by "2F"; letting PercentEncodedUrl run first matches nothing here (it wants
        // a literal "%3A"), but the ordering is pinned anyway so a later edit to either pattern
        // cannot silently make the single-encoded arm consume the prefix of a double-encoded URL and
        // strand its tail — the same fragment-leak shape the IPv6/HostWithPort ordering guards.
        text = DoubleEncodedUrl().Replace(text, Replacement);
        text = PercentEncodedUrl().Replace(text, Replacement);

        // arb-qj9: before the host arms, because a UNC path's server name carries no dot, no port
        // and no scheme — none of them can see it, and running it here keeps the whole path together
        // rather than letting a later arm take a bite out of the middle.
        text = UncPath().Replace(text, Replacement);

        // IPv6 MUST precede HostWithPort. "[fd00:1234:5678::42]:11434" contains several substrings
        // that HostWithPort matches ("1234:5678" reads as host:port), so letting it run first eats a
        // fragment and leaves the rest of the address published — the exact shape arb-fbx found.
        text = IpV6Address().Replace(text, Replacement);

        text = HostWithPort().Replace(text, Replacement);
        text = IpAddress().Replace(text, Replacement);
        text = DottedHostName().Replace(text, Replacement);

        // Runs after every other host arm on purpose: those leave a <redacted> token behind, which
        // cannot be re-captured here (the token starts with '<', outside the label character class),
        // so an already-scrubbed "upstream <redacted>" is not scrubbed twice.
        text = ContextualSingleLabelHost().Replace(
            text,
            m => m.Groups["prefix"].Value + Replacement);

        // arb-6vf: the shared credential arms, the same implementation the log cleanser runs.
        // They run AFTER the host arms for the reason given on this method: a URL carrying a
        // credential must be removed whole, rather than having its credential redacted in place and
        // its address left behind.
        text = CredentialPatterns.RedactCredentials(text);

        return text.Trim();
    }

    /// <summary>
    /// Any absolute URL, with whatever credential it carries in its query or path.
    ///
    /// <para><b>The greedy <c>\S+</c> tail is load-bearing, not stylistic.</b> The excerpt is
    /// TRUNCATED at capture (<see cref="OllamaRequestException.MaxExcerptLength"/>) and scrubbed
    /// afterwards, so a URL can arrive cut at an arbitrary offset. Because this pattern consumes
    /// everything non-whitespace after the scheme, every such prefix is still swallowed whole; a
    /// pattern anchored on URL structure (an explicit host/port/path grammar) would fail to match a
    /// truncated tail and republish the host. <c>A_truncated_url_never_leaves_a_host_behind</c> in
    /// <c>SanitizedErrorDescriptionTests</c> sweeps every cut offset and pins this.</para>
    /// </summary>
    [GeneratedRegex(
        @"\b[a-z][a-z0-9+.-]*://\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex Url();

    /// <summary>
    /// The same URL, percent-encoded — <c>http%3A%2F%2Fhost%3A11434%2Fapi%2Fchat</c>. A body that
    /// echoes back a query parameter re-encodes the URL it carried, which <see cref="Url"/> misses
    /// because there is no literal <c>://</c> left in it. Same greedy tail, for the same reason.
    /// </summary>
    [GeneratedRegex(
        @"\bhttps?%3A%2F%2F\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex PercentEncodedUrl();

    /// <summary>
    /// arb-qj9: the DOUBLE-encoded URL — <c>http%253A%252F%252Fhost%253A11434</c>. A value that has
    /// been round-tripped through a query string twice (a proxy echoing back a parameter it was
    /// itself given encoded) carries <c>%25</c> where the single-encoded form carries <c>%</c>, so
    /// neither <see cref="Url"/> nor <see cref="PercentEncodedUrl"/> sees it.
    ///
    /// <para><b>This was a PARTIAL leak, not a total one, which is why the test plants a fragment.</b>
    /// <see cref="DottedHostName"/> already matched inside the encoded text and removed the dotted
    /// middle of the name, so what actually survived was the stranded FIRST LABEL and the encoded
    /// port — "ollama" and "%253A11434" out of "ollama.internal.example:11434". Planting the whole
    /// URL and asserting its absence would therefore have passed against a scrubber with no fix at
    /// all; the row plants what genuinely survived instead (the lesson from arb-fbx's own vacuous
    /// row). Same greedy tail as the other two URL arms, for the truncation reason given on
    /// <see cref="Url"/>.</para>
    /// </summary>
    [GeneratedRegex(
        @"\bhttps?%253A%252F%252F\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex DoubleEncodedUrl();

    /// <summary>
    /// arb-qj9: a Windows UNC path — <c>\\NASBOX\media\share</c>. The first component after the
    /// leading pair of backslashes is a HOSTNAME, and nothing else in this file is shaped to see it:
    /// it carries no dot, no port and no scheme, so every host arm walked past it and the share name
    /// published along with the server name.
    ///
    /// <para>The whole path is taken, not merely the host component. The share and directory names
    /// on a NAS are themselves topology ("\\NASBOX\media\tv" says what the box is for), and this
    /// file's stated trade is that an over-scrubbed message beats a published internal name. A
    /// forward-slash path is deliberately NOT matched: "/etc/arbitarr/config.json" is ordinary
    /// diagnostic text an operator needs, and it names no host.</para>
    /// </summary>
    [GeneratedRegex(
        @"\\\\[a-z0-9_-]+(?:\\[^\s\\]+)*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex UncPath();

    /// <summary>
    /// An IPv6 literal: bracketed with an optional port (<c>[2001:db8::1]:11434</c> — the shape Go's
    /// <c>net</c> package prints, and Ollama is Go), or bare, in either the compressed
    /// (<c>fe80::1</c>) or full (<c>2001:db8:1234::42</c>) form, each with an optional
    /// <c>%zone</c> and an optional port.
    ///
    /// <para><b>Three details here are load-bearing, each from a real leak found reviewing this
    /// file's first version.</b></para>
    ///
    /// <para>1. The compressed alternative (<c>(?:hex:){1,7}:</c>) exists because requiring two
    /// <c>hex:</c> groups misses every two-group address — <c>fe80::1</c>, <c>fd00::42</c>,
    /// including the very ULA this file's own comments cite.</para>
    ///
    /// <para>2. <c>%zone</c> is matched on BOTH the bracketed and bare forms. It is outside the
    /// bracketed character class, so without it <c>[fe80::1%eth0]:11434</c> matched nothing and the
    /// whole address published.</para>
    ///
    /// <para>3. The trailing <c>\d*</c> after the optional port is not redundant. A hex group is
    /// capped at four characters, so in <c>fd00::42:11434</c> the engine reads <c>:1143</c> as a
    /// final group and leaves a bare <c>4</c> behind; the <c>\d*</c> absorbs whatever digits that
    /// cap stranded, so no fragment of a port survives beside the redaction token.</para>
    ///
    /// <para><b>Deliberately over-scrubbed:</b> a bare colon-separated run of hex-like groups is
    /// indistinguishable from a MAC address or a timestamp, so <c>aa:bb:cc</c> and <c>12:34:56</c>
    /// are redacted too. That is this file's stated trade — over-scrubbing beats publishing
    /// topology — and the negative theory in the tests pins what must NOT be eaten: a model tag
    /// ("llama3.1:8b", "phi4:14b") keeps its non-hex characters and survives, as does "HTTP 400".</para>
    /// </summary>
    [GeneratedRegex(
        @"\[[0-9a-f:.]+(?:%[a-z0-9._-]+)?\](?::\d{1,5})?" +
        @"|\b(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)?(?:%[a-z0-9._-]+)?(?::\d{1,5})?\d*" +
        @"|\b(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{1,4}(?:%[a-z0-9._-]+)?(?::\d{1,5})?\d*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex IpV6Address();

    /// <summary>
    /// A single-label host named by the word in front of it — "upstream ollama-gpu-rig",
    /// "dial mediabox", "connecting to nas". A bare label carries no dot and no port, so no
    /// structural pattern can tell it from an ordinary noun; the CONTEXT word is the only signal,
    /// and these are words that are followed by a host or by nothing useful.
    ///
    /// <para><b>The false-positive cost is accepted deliberately.</b> "upstream service" would be
    /// redacted along with "upstream ollama-gpu-rig". That is this file's stated trade: an
    /// over-scrubbed word is a smaller failure than a published internal hostname, and the operator
    /// still has the unredacted text in the log. It is why the trigger list stays SHORT and holds
    /// only connection verbs and the two prepositions that introduce a peer ("refused VIA nas",
    /// "proxied THROUGH gateway") — "model" and "error" are excluded precisely because the word after
    /// them is the useful reason, not a host.</para>
    ///
    /// <para><b>arb-qj9: the separator admits punctuation, not just whitespace.</b> Requiring
    /// <c>\s</c> after the trigger meant the three commonest real forms walked straight through —
    /// <c>upstream: ollama-gpu-rig</c>, <c>upstream "ollama-gpu-rig"</c>, and a bare
    /// <c>Host: ollama-gpu-rig</c> header line with no port for <see cref="HostWithPort"/> to catch.
    /// A header line is exactly how an upstream echoes back the host it was asked for, so this was
    /// the likeliest of the shapes the #195 review found. The quote or colon is consumed INTO the
    /// prefix group, so it survives in the output and the line still reads as a header.</para>
    ///
    /// <para><b>The separator class includes a BACKSLASH, which is not cosmetic.</b> The excerpt is
    /// a JSON body, so a quoted host arrives ESCAPED — <c>upstream \"ollama-gpu-rig\"</c>, i.e. the
    /// characters between the trigger and the host are backslash-then-quote. A class of
    /// <c>["':=]</c> alone matched the bare form in a hand-written probe and MISSED the real wire
    /// form; the escaped row in <c>The_shapes_the_security_review_found_no_longer_publish</c> is
    /// what caught it.</para>
    ///
    /// <para><b>arb-qj9: the value class admits <c>_</c>.</b> Docker Compose service names routinely
    /// carry underscores ("gpu_box_example"), and they are hostnames on the container network — the
    /// single most likely single-label host to appear in an error from a containerised Ollama.</para>
    ///
    /// <para><b>arb-qj9: <c>tcp</c>/<c>udp</c> are skipped after the trigger, not taken as the
    /// host.</b> Go's network errors read <c>dial tcp gpu_box_example:11434: refused</c>, and the arm
    /// as written consumed "tcp" as the value — publishing <c>dial &lt;redacted&gt; gpu_box_example</c>,
    /// which redacted the protocol name and left the real host standing. That mis-fire PREDATES this
    /// change (the trigger list already carried <c>dial</c>, and <c>\s+</c> already reached "tcp"); it
    /// is fixed here because this is the arm being widened and the wrong output is this arm's own.
    /// The protocol is consumed INTO the prefix, so it survives and the line still reads as a dial
    /// error. <c>HostWithPort</c> catches the host when a port is present; this makes the portless
    /// form work too.</para>
    ///
    /// <para><b>The <c>(?!(?:tcp|udp)[46]?\b)</c> after the protocol keeps the arm IDEMPOTENT, and it
    /// is the whole reason that lookahead is there.</b> Without it, a second pass over
    /// already-scrubbed <c>dial tcp &lt;redacted&gt;</c> finds the optional skip followed by
    /// <c>&lt;redacted&gt;</c>, which the value class cannot match; the engine then BACKTRACKS,
    /// discards the skip, and takes "tcp" as the value — re-introducing the very mis-fire this change
    /// removes, on the second pass instead of the first. The remark above on the replacement token
    /// not being re-capturable holds only because no arm can reach PAST it; an optional group that
    /// may be discarded is exactly how an arm reaches past it. <c>Describe</c> scrubs a body that was
    /// already scrubbed at construction, so the second pass is the normal path here, not a
    /// hypothetical.</para>
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:upstream|host|dial|peer|via|through|connect(?:ing|ed)?\s+to|resolve|resolving|lookup)(?:\s|[""':=\\])+(?:(?:tcp|udp)[46]?\s+)?)(?!(?:tcp|udp)[46]?\b)(?<value>[a-z0-9][a-z0-9_-]{2,62})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex ContextualSingleLabelHost();

    /// <summary>
    /// A bare <c>host:port</c> — the shape an <see cref="System.Net.Http.HttpRequestException"/>
    /// message uses ("(ollama.internal.example:11434)"), and the reason this type exists.
    ///
    /// <para><b>arb-qj9: labels may contain <c>_</c>.</b> A Docker Compose service name
    /// ("gpu_box_example:11434") is a hostname on the container network and is the likeliest host to
    /// appear in an error from a containerised Ollama, but underscore is outside the DNS label
    /// character class this pattern started from, so the whole <c>host:port</c> published. Admitting
    /// it here costs nothing — the <c>:port</c> is still required, which is what keeps this arm off
    /// ordinary prose.</para>
    /// </summary>
    [GeneratedRegex(
        @"\b(?:[a-z0-9](?:[a-z0-9_-]*[a-z0-9])?\.)*[a-z0-9](?:[a-z0-9_-]*[a-z0-9])?:\d{1,5}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex HostWithPort();

    /// <summary>A dotted-quad IPv4 address, with or without a port.</summary>
    [GeneratedRegex(
        @"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex IpAddress();

    /// <summary>
    /// A dotted host NAME with no port and no scheme. The rule has two halves, and BOTH are needed:
    ///
    /// <para><b>Half one — three-or-more labels</b> ("ollama.internal.example"). Two dots minimum is
    /// what keeps an ordinary sentence's "duration." and a decimal number out of it; the alphabetic
    /// final label (<c>[a-z]{2,}</c>) is what lets a version string "1.2.3" survive.</para>
    ///
    /// <para><b>Half two — two labels when the final one is a known private-network suffix</b>
    /// ("ollama.lan", "nas.local", "box.home"). These are the names a home LAN actually uses, and
    /// half one alone published every one of them (arb-fbx). The suffix must be enumerated rather
    /// than "any two labels", because relaxing to two labels unconditionally would eat the model tags
    /// and file names an operator needs — "qwen2.5", "config.json". The <c>[a-z]{2,}</c> guard still
    /// applies to both halves.</para>
    ///
    /// <para><b>arb-qj9: the suffix list gained <c>box</c>, <c>localhost</c>, <c>onion</c> and
    /// <c>lokal</c>, and labels may contain <c>_</c>.</b> The #195 security review found these
    /// leaking: they are private-network suffixes in exactly the sense the list already covers
    /// (<c>.box</c> ships as the default on several consumer routers, <c>.localhost</c> resolves
    /// loopback, <c>.onion</c> names a hidden service), so their absence was an omission rather than
    /// a decision. ENUMERATING remains the right shape — "any two labels" is still what would eat
    /// "qwen2.5" and "config.json", and this list can only ever be extended by naming a suffix,
    /// which is a reviewable act.</para>
    /// </summary>
    [GeneratedRegex(
        @"\b(?:(?:[a-z0-9](?:[a-z0-9_-]*[a-z0-9])?\.){2,}[a-z]{2,}|[a-z0-9](?:[a-z0-9_-]*[a-z0-9])?\.(?:lan|local|lokal|localhost|home|box|onion|internal|intranet|corp|arpa|localdomain|test|invalid|example))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.MatchTimeoutMilliseconds)]
    private static partial Regex DottedHostName();
}
