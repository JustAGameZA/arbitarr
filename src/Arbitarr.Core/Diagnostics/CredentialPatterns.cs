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
    /// <para>arb-ofz6: <see cref="NamedCredential"/> now runs on the non-backtracking engine, which
    /// closes its specific quadratic case, but this timeout still guards the other three arms and
    /// remains NamedCredential's own ceiling as defence in depth against any shape not yet found.</para>
    ///
    /// <para>arb-0na2: those other three arms have since been measured against the adversarial input
    /// for each one's OWN prefix shape and are linear, so none of them was switched — this timeout is
    /// not currently load-bearing for any known shape in these four shared arms. This value is also
    /// shared with <see cref="SanitizedErrorDescription"/>'s local arms (see the arb-hihr remark
    /// below); arb-0na2 did not measure those, so no claim is made about them here. Keep the timeout
    /// anyway: it is the ceiling for the shapes nobody has found yet, in all four shared arms, and it
    /// is what makes <c>LogMessageCleanser</c>'s per-row fail-closed path reachable at all.</para>
    ///
    /// <para>arb-hihr: <c>internal</c> rather than <c>private</c> so <see cref="SanitizedErrorDescription"/>'s
    /// nine local host/URL arms — which face the same attacker-influenced <c>/api/status</c> input as
    /// these four shared credential arms, per that file's remarks — reference the SAME value instead
    /// of drifting to their own copy of the literal.</para>
    /// </summary>
    internal const int MatchTimeoutMilliseconds = 250;

    /// <summary>
    /// arb-mw7: public alias of <see cref="MatchTimeoutMilliseconds"/> for
    /// <c>Arbitarr.Data.Logging.LogMessageCleanser</c>'s local <c>WebhookUrl</c> arm — Data has no
    /// <c>InternalsVisibleTo</c> from this project, so the <c>internal</c> constant is not visible
    /// there. Kept as a second const, not a change to the original's accessibility, so nothing else
    /// in Core widens.
    /// </summary>
    public const int PublicMatchTimeoutMilliseconds = MatchTimeoutMilliseconds;

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
    ///
    /// <para><b>arb-0na2: backtracking engine, deliberately — measured, not assumed.</b> This arm
    /// does NOT carry <see cref="NamedCredential"/>'s <c>\b[\w-]*</c> variable-length prefix beside
    /// the keyword, which is the structure that made that arm quadratic. Its prefix is anchored on a
    /// literal <c>[?&amp;]</c>, so each candidate start is a single character the engine can reject
    /// or commit on immediately. Measured outside this repo against the adversarial shape for THIS
    /// prefix — a dense field of <c>?</c>/<c>&amp;</c> starts each followed by near-keyword text that
    /// fails as late as possible: it is linear from 2k to 32k characters, with the time merely
    /// doubling as the input doubles. The same harness run drove the pre-#546 backtracking
    /// <see cref="NamedCredential"/> pattern as a positive control and saw it grow roughly fivefold
    /// per doubling and reach <see cref="MatchTimeoutMilliseconds"/>, so the linear result here is
    /// evidence, not an unexercised harness. No engine switch was applied. Pinned by
    /// <c>CredentialPatternsTests.An_adversarial_run_for_each_shared_arms_own_prefix_shape_completes_and_still_redacts</c>.</para>
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
    ///
    /// <para><b>arb-0na2: backtracking engine, deliberately — measured, not assumed.</b> Like
    /// <see cref="QueryParameterCredential"/> and unlike <see cref="NamedCredential"/>, this arm has
    /// no variable-length prefix beside its keyword: <c>\b(?:bearer|basic)\s+</c> is a word-boundary
    /// anchor followed by two fixed literals. Measured outside this repo at 2k, 4k, 8k, 16k and 32k
    /// characters against both adversarial shapes for THIS arm — a dense field of
    /// <c>bearer</c>/<c>basic</c> near-misses at word boundaries, and one keyword followed by an
    /// unterminated run of this arm's own <c>[A-Za-z0-9+/=._~-]</c> value class (the shape that would
    /// backtrack if a variable value sat next to a variable prefix): it is linear in both from 2k to
    /// 32k characters, the time merely doubling as the input doubles. The same run's positive control
    /// (the pre-#546 backtracking <see cref="NamedCredential"/> pattern) grew roughly fivefold per
    /// doubling and did reach <see cref="MatchTimeoutMilliseconds"/>, so the harness demonstrably
    /// observes a blowup when one exists. No engine switch was applied.
    /// Pinned by the two <c>arb-0na2</c> adversarial theories in <c>CredentialPatternsTests</c>.</para>
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
    ///
    /// <para>This is why <c>Arbitarr:ReleaseGuidSecret</c> gets redacted at all: the setting NAME
    /// contains the literal <c>secret</c>, so this pattern matches it even though the value itself
    /// is ordinary base64 with no distinguishing shape. Renaming that setting away from every word
    /// in the alternation below silently drops the coverage. See
    /// <c>ProductionProcessGlobalStateTests</c> in <c>Arbitarr.Architecture.Tests</c> for the fuller
    /// account.</para>
    ///
    /// <para><b>arb-cia3: the <c>plaintext…</c> alternative, and why it is suffixed.</b> The name
    /// <c>PlaintextKey</c> matched NOTHING here before — <c>key</c> alone is deliberately excluded
    /// from the alternation (that exclusion is what keeps a release GUID readable), and
    /// "plaintext" is not one of the other six words. So the synthesised <c>ToString</c> of
    /// <c>CreatedApiKey</c> / <c>CreatedApiKeyResponse</c> rendered a live admin key past BOTH
    /// sinks verbatim. Note the asymmetry it exposed: <c>PlaintextToken</c> and
    /// <c>PlaintextPassword</c> were always covered, because they carry <c>token</c> and
    /// <c>password</c> — only the <c>…Key</c> spelling fell through.</para>
    ///
    /// <para>The alternative requires a credential NOUN after the word rather than being a bare
    /// <c>plaintext</c>, because "plaintext" is ordinary prose in this codebase: three probe
    /// outcomes say "https pointed at a plaintext port". Measured, a bare alternative redacted
    /// "TLS disabled, plaintext: true" into "plaintext: &lt;redacted&gt;" — over-redaction is the
    /// failure this type's own remarks warn a new pattern must be tested against. The negative rows
    /// in <c>CredentialPatternsTests.Ordinary_text_is_left_intact</c> pin that.</para>
    ///
    /// <para>This is defence in depth ONLY. The primary control is the <c>ToString</c> override on
    /// each secret-bearing record (arb-1ox9, swept by <c>CredentialRecordToStringTests</c>);
    /// removing an override because this arm now catches its rendering would trade a source-side
    /// guarantee for a sink-side denylist, which is exactly the inversion the type's remarks above
    /// caution against.</para>
    ///
    /// <para><b>arb-ofz6: <c>RegexOptions.NonBacktracking</c>, pattern text unchanged.</b> The
    /// leading <c>[\w-]*</c> sitting next to the keyword alternation makes this arm's prefix scan
    /// quadratic against a long separator-less <c>[\w-]</c> run with enough character variety —
    /// near-keyword text (<c>apikey</c> repeated) is one instance, but so is a repeated different
    /// keyword (<c>secret</c>, <c>a-token-b-</c>) and even a run with NO keyword in it at all, as
    /// long as it cycles through enough distinct characters (measured: a run cycling
    /// <c>abcdefghijklmnopqrstuvwxyz0123456789_-</c> reaches the timeout too). A single REPEATED
    /// character (e.g. plain <c>aaaa…</c>) does NOT trigger it — there has to be enough variety for
    /// the prefix scan to keep finding new candidate start points. Reliably reaches
    /// <see cref="MatchTimeoutMilliseconds"/> at lengths well inside
    /// <c>LogMessageCleanser.MaxCleanseInputLength</c>. Measured outside this repo: a 400,000-input
    /// differential fuzz between the backtracking and non-backtracking engines over this exact
    /// pattern produced zero output differences, because the pattern has no lookarounds,
    /// backreferences, or atomic groups — the constructs .NET's non-backtracking engine cannot
    /// execute. Switching the engine is therefore behaviour-preserving here.</para>
    ///
    /// <para><b>Do NOT "optimise" the pattern text instead.</b> Two alternatives were tried and
    /// REJECTED because both scrub LESS: an atomic group (<c>(?>[\w-]*)</c>) and a bounded prefix
    /// (e.g. <c>[\w-]{0,8}</c>). Both leave a credential in the clear when the keyword is preceded by
    /// more name characters than the bound allows and there is no earlier word boundary the engine
    /// can restart from — e.g. <c>vendorlongprefixapikey:</c>, where the greedy backtracking prefix
    /// (and the non-backtracking engine, which explores the same language) still finds the keyword
    /// and matches through to the separator, but an atomic or too-tightly-bounded prefix commits too
    /// early and misses it. A bound that is merely generous (e.g. <c>{0,8}</c>) can still pass a row
    /// with a hyphen or underscore earlier in the name, because <c>\b</c> lets the engine restart the
    /// match closer to the keyword — <c>x-secret-header-name:</c> and <c>a-token-b-apikey-c:</c>
    /// pass even a `{0,8}` bound for exactly that reason, so they do not by themselves prove a bound
    /// is safe; <c>vendorlongprefixapikey:</c>, with no boundary between the excess prefix and the
    /// keyword, is the row that actually discriminates. See <c>CredentialPatternsTests</c>'s
    /// prefix-semantics rows for the cases this would break.</para>
    ///
    /// <para><b>Why this still compiles with <c>[GeneratedRegex]</c>.</b> The source generator does
    /// not emit generated matching code for a pattern combined with <c>RegexOptions.NonBacktracking</c>;
    /// it instead emits a thin wrapper that constructs and caches a plain, non-generated
    /// <see cref="Regex"/> instance at first use — silently, with no compiler warning, because this is
    /// documented generator behaviour rather than a failure to generate. That is acceptable here: this
    /// arm is one static, process-lifetime instance (not constructed per-call), and this repository
    /// does not publish trimmed or Native AOT — the two scenarios where losing generated (reflection-
    /// free, trimming-safe) code would matter. If either changes, this arm's degrade-to-reflection cost
    /// is the thing to re-examine, not the correctness of the redaction.</para>
    ///
    /// <para><b>arb-0na2: this remains the ONLY arm on the non-backtracking engine.</b> The other
    /// three shared arms, and the cleanser's local <c>WebhookUrl</c> arm, were each measured against
    /// the adversarial input for their OWN prefix shape and are linear, so none of them was switched.
    /// The discriminator is this arm's <c>\b[\w-]*</c> sitting beside the keyword alternation: it is
    /// the only shared pattern with a variable-length prefix adjacent to the literal it must find,
    /// which is what lets a single long run supply a fresh candidate start at every position. An arm
    /// that later grows such a prefix inherits the same hazard and should be re-measured, not assumed
    /// safe by analogy with these three.</para>
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b[\w-]*(?:api[_-]?key|apikey|token|passkey|password|secret|plaintext[_-]?(?:key|token|secret|password|passkey))[\w-]*""?\s*[:=]\s*""?)(?<value>[^\s,;""'}\]]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
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
    ///
    /// <para><b>arb-0na2: backtracking engine, deliberately — measured, not assumed.</b> This arm is
    /// the one arb-0na2 was opened on by name, because it is the closest sibling of
    /// <see cref="NamedCredential"/>: same keyword alternation, same credential nouns. The structural
    /// difference is the one that matters — this arm has NO <c>[\w-]*</c> before the alternation, so
    /// its prefix is a word boundary followed immediately by a fixed literal, and a long run offers
    /// the engine no extra candidate start points to explore. Measured outside this repo at 2k, 4k,
    /// 8k, 16k and 32k characters against three adversarial shapes for THIS arm — a dense field of
    /// bare credential nouns at word boundaries each failing on the <c>{12,}</c> value floor, one
    /// keyword followed by an unterminated run of this arm's own <c>[A-Za-z0-9_.+-]</c> value class,
    /// and the separator-less variety run that defeats <see cref="NamedCredential"/> — it is linear
    /// in all three from 2k to 32k characters, the time merely doubling as the input doubles.
    /// The same run's positive control (the pre-#546 backtracking <see cref="NamedCredential"/>
    /// pattern) grew roughly fivefold per doubling and then reached
    /// <see cref="MatchTimeoutMilliseconds"/>, so the harness was demonstrably capable of seeing a
    /// blowup here and did not. No engine switch was
    /// applied, and none should be applied without a measurement that shows one is needed: the
    /// non-backtracking engine is not free, and this arm has no case to spend it on. Pinned by the
    /// two <c>arb-0na2</c> adversarial theories in <c>CredentialPatternsTests</c>.</para>
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:api[_-]?key|apikey|key|token|secret|password|passkey)\s+)(?<value>[A-Za-z0-9_.+-]{12,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: MatchTimeoutMilliseconds)]
    private static partial Regex SpaceSeparatedCredential();
}
