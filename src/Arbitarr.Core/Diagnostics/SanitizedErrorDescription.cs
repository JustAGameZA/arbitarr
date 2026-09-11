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
    /// </summary>
    internal static string ScrubForPublication(string? excerpt)
    {
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            return string.Empty;
        }

        var text = excerpt;

        text = Url().Replace(text, Replacement);
        text = PercentEncodedUrl().Replace(text, Replacement);

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
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    /// <summary>
    /// The same URL, percent-encoded — <c>http%3A%2F%2Fhost%3A11434%2Fapi%2Fchat</c>. A body that
    /// echoes back a query parameter re-encodes the URL it carried, which <see cref="Url"/> misses
    /// because there is no literal <c>://</c> left in it. Same greedy tail, for the same reason.
    /// </summary>
    [GeneratedRegex(
        @"\bhttps?%3A%2F%2F\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PercentEncodedUrl();

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
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:upstream|host|dial|peer|via|through|connect(?:ing|ed)?\s+to|resolve|resolving|lookup)\s+)(?<value>[a-z0-9][a-z0-9-]{2,62})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContextualSingleLabelHost();

    /// <summary>
    /// A bare <c>host:port</c> — the shape an <see cref="System.Net.Http.HttpRequestException"/>
    /// message uses ("(ollama.internal.example:11434)"), and the reason this type exists.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)*[a-z0-9](?:[a-z0-9-]*[a-z0-9])?:\d{1,5}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostWithPort();

    /// <summary>A dotted-quad IPv4 address, with or without a port.</summary>
    [GeneratedRegex(
        @"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?\b",
        RegexOptions.CultureInvariant)]
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
    /// </summary>
    [GeneratedRegex(
        @"\b(?:(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.){2,}[a-z]{2,}|[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.(?:lan|local|home|internal|intranet|corp|arpa|localdomain|test|invalid|example))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DottedHostName();
}
