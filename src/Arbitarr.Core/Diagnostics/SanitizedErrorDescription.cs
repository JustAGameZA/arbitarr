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
/// <para><b>Why the scrubbing is implemented here rather than calling <c>LogMessageCleanser</c>.</b>
/// That cleanser lives in Arbitarr.Data, which references Arbitarr.Core; Core referencing it back
/// would be a reference cycle and would break AC6 (<c>CoreIsolationTests</c> asserts Core references
/// no other Arbitarr project). The credential patterns below are therefore kept deliberately
/// EQUIVALENT to that cleanser's, and use the same <c>&lt;redacted&gt;</c> replacement token so a
/// reader sees one vocabulary across both sinks. If a pattern is added there for a real leak, add it
/// here too — the two lists are siblings, and <c>SanitizedErrorDescriptionTests</c> pins the shared
/// token so a divergence in that much at least fails a test.</para>
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
    /// The text substituted for a redacted value. Matches <c>LogMessageCleanser.Replacement</c>
    /// deliberately — see the note above on why the two are siblings rather than one shared call.
    /// </summary>
    public const string Replacement = "<redacted>";

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

        text = QueryParameterCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = AuthorizationScheme().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = NamedCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = SpaceSeparatedCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);

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
    /// An IPv6 literal, bracketed with an optional port (<c>[2001:db8::1]:11434</c> — the shape Go's
    /// <c>net</c> package prints, and Ollama is Go) or bare (<c>2001:db8::1</c>). Requires at least
    /// two colon-separated groups in the bare form so an ordinary <c>key:value</c> or a model tag
    /// ("phi4:14b") is not mistaken for an address.
    /// </summary>
    [GeneratedRegex(
        @"\[[0-9a-f:.]+\](?::\d{1,5})?|\b(?:[0-9a-f]{1,4}:){2,7}[0-9a-f:]{1,4}\b",
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

    /// <summary>A credential carried as a URL query parameter, per <c>LogMessageCleanser</c>.</summary>
    [GeneratedRegex(
        @"(?<prefix>[?&](?:api_?key|token|passkey|password)=)(?<value>[^&\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryParameterCredential();

    /// <summary>An <c>Authorization</c>-style scheme-prefixed token, per <c>LogMessageCleanser</c>.</summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:bearer|basic)\s+)(?<value>[A-Za-z0-9+/=._~-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    /// <summary>A named credential assigned inline, per <c>LogMessageCleanser</c>.</summary>
    [GeneratedRegex(
        @"(?<prefix>\b[\w-]*(?:api[_-]?key|apikey|token|passkey|password|secret)[\w-]*""?\s*[:=]\s*""?)(?<value>[^\s,;""'}\]]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedCredential();

    /// <summary>
    /// The same named credential written with a SPACE instead of <c>:</c> or <c>=</c> — "invalid key
    /// sk-live-9f8e...". <see cref="NamedCredential"/> requires the separator, so prose forms walked
    /// straight through it. The 12-character floor on the value keeps ordinary prose ("token expired")
    /// intact while still catching anything key-shaped.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:api[_-]?key|apikey|key|token|secret|password|passkey)\s+)(?<value>[A-Za-z0-9_-]{12,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpaceSeparatedCredential();
}
