using System.Text.RegularExpressions;

namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// arb-6vf: the ONE implementation of credential redaction, shared by both sinks that need it —
/// <c>Arbitarr.Data.Logging.LogMessageCleanser</c> (the log database) and
/// <see cref="SanitizedErrorDescription"/> (the unauthenticated <c>GET /api/status</c> payload).
///
/// <para><b>Why this type exists.</b> The two sinks previously held their own copies of these
/// patterns, kept "deliberately equivalent" by a comment asking the next contributor to remember to
/// edit both. They had already drifted: <see cref="SpaceSeparatedCredential"/> was added to the
/// scrubber under arb-fbx and never reached the cleanser, so a prose-form credential
/// ("invalid key sk-PLACEHOLDER-9f8e…") was redacted off the public dashboard while still being
/// written verbatim into the log store. A convention that depends on memory is not a control; one
/// call site is.</para>
///
/// <para><b>Why it lives in Core.</b> <c>Arbitarr.Data</c> references <c>Arbitarr.Core</c> and Core
/// references no other Arbitarr project (AC6, pinned by <c>CoreIsolationTests</c>). Core is
/// therefore the only place BOTH consumers can reach; the cleanser cannot own the shared code
/// because Core calling back into Data would be a reference cycle. That constraint is the whole
/// reason the duplication existed, and putting the shared half here is what removes it.</para>
///
/// <para><b>What is deliberately NOT shared: the host arms.</b> Only credentials are common.
/// <see cref="SanitizedErrorDescription"/> additionally strips anything host-shaped, because its
/// output reaches an unauthenticated caller and LAN topology is the specific harm it exists to
/// prevent. The cleanser must NOT gain those arms: <c>/api/admin/logs</c> is admin-gated and an
/// operator diagnosing a connection failure needs the hostname that failed. Scrubbing it there
/// would remove the diagnostic value of the log without protecting anyone who is not already
/// authenticated.</para>
///
/// <para>This is defence in depth, not the control — see the remarks on
/// <c>LogMessageCleanser</c> for why the allowlist-at-the-source posture is the primary
/// protection and why this list stays short. Adding a pattern here now widens BOTH sinks at once,
/// which is the point, but it is also why a new pattern needs a test proving it does not
/// over-redact the ordinary text either sink still has to stay readable.</para>
/// </summary>
public static partial class CredentialPatterns
{
    /// <summary>
    /// The text substituted for a redacted value. Both sinks re-export this as their own
    /// <c>Replacement</c> const so existing call sites and tests keep compiling, and so a reader
    /// sees one vocabulary across both.
    /// </summary>
    public const string Replacement = "<redacted>";

    /// <summary>
    /// arb-qj9 (#206 security review): upper bound on a single match, matching the repo's posture
    /// for regexes that run over untrusted input (<c>FilterRule.MatchTimeout</c>, also 250ms).
    ///
    /// <para>The risk here is the inverse of <c>FilterRule</c>'s. There the PATTERN is user-supplied
    /// and the timeout bounds a hostile regex; these patterns are compile-time constants, but the
    /// INPUT is an upstream response body an attacker may influence, and several arms carry a
    /// variable-length value class adjacent to a variable-length prefix — the shape that backtracks.
    /// A scrubber that hangs is a worse outage than one that throws: an unbounded match on a log
    /// write stalls the writer, and this runs on the path that serves <c>/api/status</c>.</para>
    ///
    /// <para>250ms is enormous for an excerpt this size, so it should never fire on legitimate
    /// input; it exists as a ceiling, not a tuning knob.</para>
    ///
    /// <para>arb-hihr: <c>internal</c> rather than <c>private</c> so <see cref="SanitizedErrorDescription"/>'s
    /// nine local host/URL arms — which face the same attacker-influenced <c>/api/status</c> input as
    /// these four shared credential arms, per that file's remarks — reference the SAME value instead
    /// of drifting to their own copy of the literal.</para>
    /// </summary>
    internal const int MatchTimeoutMilliseconds = 250;

    /// <summary>
    /// Returns <paramref name="text"/> with every credential-shaped substring replaced by
    /// <see cref="Replacement"/>, preserving the NAME that introduced each one.
    ///
    /// <para>The prefix group is kept on purpose: "apikey=&lt;redacted&gt;" tells an operator which
    /// credential was present, where "&lt;redacted&gt;" alone would leave them unable to tell an
    /// api key from a password in the text they are reading. Only the value is removed.</para>
    ///
    /// <para>Order within this method is not load-bearing the way the host arms' order is — these
    /// four patterns match disjoint shapes — but it is kept stable so both sinks produce
    /// byte-identical output to what they produced before this type existed.</para>
    /// </summary>
    public static string RedactCredentials(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        text = QueryParameterCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = AuthorizationScheme().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = NamedCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = SpaceSeparatedCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);

        return text;
    }

    /// <summary>
    /// A credential carried as a URL query parameter — <c>?apikey=…</c>, <c>&amp;api_key=…</c>,
    /// <c>token</c>, <c>passkey</c>, <c>password</c>. This is the shape most likely to appear here
    /// by accident, because it survives being embedded in an exception's request URI, which is text
    /// no call site deliberately composed.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>[?&](?:api_?key|token|passkey|password)=)(?<value>[^&\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: MatchTimeoutMilliseconds)]
    private static partial Regex QueryParameterCredential();

    /// <summary>
    /// An <c>Authorization</c>-style header value, either a scheme-prefixed token
    /// (<c>Bearer …</c>, <c>Basic …</c>) or a <c>key: value</c> / <c>key=value</c> pair whose name
    /// is credential-shaped. Header dumps are the other text an exception drags along unbidden.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:bearer|basic)\s+)(?<value>[A-Za-z0-9+/=._~-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: MatchTimeoutMilliseconds)]
    private static partial Regex AuthorizationScheme();

    /// <summary>
    /// A named credential assigned inline: <c>X-Admin-Api-Key: abc…</c>, <c>apikey=abc…</c>,
    /// <c>"password": "abc…"</c>. Deliberately requires a credential-shaped NAME rather than
    /// matching any long token, so ordinary identifiers (a release GUID, a commit SHA) are not
    /// mangled into unreadability — a log line redacted into uselessness is its own outage.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b[\w-]*(?:api[_-]?key|apikey|token|passkey|password|secret)[\w-]*""?\s*[:=]\s*""?)(?<value>[^\s,;""'}\]]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: MatchTimeoutMilliseconds)]
    private static partial Regex NamedCredential();

    /// <summary>
    /// The same named credential written with a SPACE instead of <c>:</c> or <c>=</c> — "invalid key
    /// sk-live-9f8e…". <see cref="NamedCredential"/> requires the separator, so prose forms walked
    /// straight through it. The 12-character floor on the value keeps ordinary prose ("token expired")
    /// intact while still catching anything key-shaped.
    ///
    /// <para><b>arb-qj9: the value class includes <c>.</c> and <c>+</c>.</b> Without them a
    /// dot-or-plus-separated key ("sk.live+PLACEHOLDER.9f8e") was not matched as one run: the
    /// character class stopped at the first <c>.</c>, leaving the remainder — the high-entropy half —
    /// published beside a redaction of the first fragment. Both characters appear in real key
    /// formats (base64url padding aside, vendors use both as segment separators), and admitting them
    /// cannot widen this arm onto ordinary prose because the credential-shaped NAME and the
    /// 12-character floor still gate it.</para>
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:api[_-]?key|apikey|key|token|secret|password|passkey)\s+)(?<value>[A-Za-z0-9_.+-]{12,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: MatchTimeoutMilliseconds)]
    private static partial Regex SpaceSeparatedCredential();
}
