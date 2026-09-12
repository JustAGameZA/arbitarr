using System.Text.RegularExpressions;
using Arbitarr.Core.Ai;
using Arbitarr.Core.Diagnostics;

namespace Arbitarr.Data.Logging;

/// <summary>
/// Redacts credential-shaped substrings from a log message or exception text before it is written
/// to the log database.
///
/// THIS IS DEFENCE IN DEPTH, NOT THE CONTROL. Read this before extending it, because the natural
/// instinct — "a leak got through, add a regex" — is the failure mode this comment exists to
/// prevent becoming the whole strategy.
///
/// Sonarr's equivalent (<c>CleanseLogMessage</c>) is roughly twenty-five regexes accumulated over a
/// decade of finding leaks in production: apikey/token/passkey query params, tracker passkeys,
/// home-directory usernames, Discord/Telegram webhook URLs, remote-IP masking. It works for Sonarr
/// because those ten years actually happened. A denylist only ever catches the patterns somebody
/// already watched leak, and Arbitarr would be starting that clock at zero while already holding
/// source API keys and (via #57) webhook URLs — so porting the list wholesale would buy the
/// *appearance* of Sonarr's protection without the decade of incidents that earned it.
///
/// Arbitarr's real advantage is that it does not have to log secrets in the first place, and the
/// codebase already holds that line structurally rather than textually:
///   - <c>source:{id}:api_key</c> rows are unreachable from the <c>SettingsCatalog</c> projection;
///   - <c>HasApiKeyAsync</c> returns a bool, so "is it configured" cannot carry the value;
///   - <c>SourceProbeOutcome</c> is a closed enum with no string field, precisely so a probe
///     failure cannot smuggle key-derived text into a message;
///   - <c>SearchRecentLogTests</c> asserts a client apikey never reaches a response body.
///
/// That allowlist-at-the-source posture is the primary control, and <c>LogSecretInjectionTests</c>
/// is the guard that actually holds it: it puts a known key through the real pipeline and asserts it
/// appears in no log row. This class is the second layer, for the unknown-unknowns that posture
/// misses. Keep it small and cheap; if you find yourself adding another pattern because something
/// leaked, the leak is the bug — fix the call site that logged the secret, then add the pattern.
///
/// <para><b>arb-6vf: the credential patterns are no longer defined here.</b> They live in
/// <see cref="CredentialPatterns"/> in Arbitarr.Core, which is the one place both this sink and the
/// unauthenticated status scrubber (<see cref="SanitizedErrorDescription"/>) can reach — Core
/// cannot reference Data back without breaking AC6. Add a credential pattern THERE and both sinks
/// gain it; the copies kept in step by convention had already drifted once. Only
/// <see cref="WebhookUrl"/> remains local, because it is specific to this sink.</para>
/// </summary>
public static partial class LogMessageCleanser
{
    /// <summary>
    /// The text substituted for a redacted value.
    ///
    /// <para>arb-6vf: aliases <see cref="CredentialPatterns.Replacement"/> rather than repeating the
    /// literal, so the two sinks cannot drift apart on the token itself. Kept as a public const
    /// because call sites and tests across the solution reference it by this name.</para>
    /// </summary>
    public const string Replacement = CredentialPatterns.Replacement;

    /// <summary>
    /// arb-mw7: substituted for a single row's text (message or exception, independently) when
    /// scrubbing that text could not complete within
    /// <see cref="CredentialPatterns.PublicMatchTimeoutMilliseconds"/>. Aliases
    /// <see cref="SanitizedErrorDescription.TimeoutPlaceholder"/> rather than minting a second
    /// literal, the same way <see cref="Replacement"/> above aliases
    /// <see cref="CredentialPatterns.Replacement"/> — Data already references Core, so there is
    /// nothing stopping the two placeholders from being the same string.
    ///
    /// <para>Unlike the status path (whole-description fail-closed, arb-hihr), the failure unit here
    /// is one row: <see cref="LogStore.WriteAsync(IReadOnlyList{PendingLogEntry}, Func{string, string}, CancellationToken)"/>
    /// writes many rows in one batch transaction, and a
    /// timeout on one row's text must not discard the other rows in the batch (arb-qafw). Losing this
    /// one row's message/exception to a fixed marker is an acceptable, bounded cost; losing the whole
    /// batch is not.</para>
    /// </summary>
    public const string TimeoutPlaceholder = SanitizedErrorDescription.TimeoutPlaceholder;

    /// <summary>
    /// arb-mw7: the fixed marker appended after a cleansed input is cut at
    /// <see cref="MaxCleanseInputLength"/>. Distinct from <see cref="TimeoutPlaceholder"/>: this
    /// replaces only the DROPPED TAIL, not the whole text, so an operator reading a long but
    /// otherwise healthy log line still sees the scrubbed head.
    /// </summary>
    public const string TruncationMarker = "…<truncated>";

    /// <summary>
    /// arb-mw7 (#206 security review): the longest input <see cref="Cleanse(string?)"/> will scrub in full.
    /// Text beyond this length is DROPPED (not merely left unscrubbed) and replaced with
    /// <see cref="TruncationMarker"/>, so a credential in the dropped tail can never reach the log
    /// store regardless of whether it would have matched.
    ///
    /// <para>16 KiB comfortably holds a full exception with stack trace — measured against this
    /// codebase's own failing-request exceptions (a few KB at most, well short of this bound) — while
    /// still capping the pathological case: an attacker-influenced upstream body pasted wholesale
    /// into a log message, which is the same growth this bead's #212 sibling (the shared arms'
    /// <c>MatchTimeoutMilliseconds</c>) bounds on time rather than size.</para>
    ///
    /// <para><b>Why no 4x-overscrub margin like <see cref="OllamaRequestException.MaxScrubInputLength"/>
    /// on the status path.</b> That path truncates a value that is then PUBLISHED, so a credential cut
    /// exactly at the boundary must still be recognisable to the regex once cut; overscrubbing before
    /// truncating closes that gap. Here nothing beyond the cut is ever persisted — the tail is
    /// discarded outright, not merely unscrubbed-and-kept — so the only possible residue is a
    /// credential's own LEADING fragment that happened to start before the cut and would have matched
    /// had the rest of it still been present. This implementation closes even that: it scrubs
    /// <see cref="MaxCleanseInputLength"/> + <see cref="OverscrubMargin"/> characters before cutting at
    /// <see cref="MaxCleanseInputLength"/>, so a credential straddling the boundary is fully inside the
    /// scrubbed window and gets redacted before the cut discards its tail. The shortest value class
    /// among the shared arms is <see cref="CredentialPatterns"/>'s <c>NamedCredential</c>, gated at
    /// <c>{4,}</c>; a margin larger than that leaves no unscrubbed straddling case at all.</para>
    /// </summary>
    public const int MaxCleanseInputLength = 16384;

    /// <summary>
    /// arb-mw7: extra characters scrubbed past <see cref="MaxCleanseInputLength"/> before the cut, so
    /// a credential straddling the boundary is redacted (not merely partially matched) before its
    /// tail is discarded. See the remarks on <see cref="MaxCleanseInputLength"/>.
    /// </summary>
    private const int OverscrubMargin = 64;

    /// <summary>
    /// A webhook URL's secret path segment — Discord and Telegram both put the credential in the
    /// PATH, not a query parameter, so the patterns above cannot see it. #57 will store webhook
    /// targets as secrets "in the same sense as a source API key"; this is here ahead of that so
    /// the sink is not the thing that has to change when #57 lands.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>https?://(?:[\w.-]*discord(?:app)?\.com/api/webhooks/|api\.telegram\.org/bot))(?<value>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: CredentialPatterns.PublicMatchTimeoutMilliseconds)]
    private static partial Regex WebhookUrl();

    /// <summary>
    /// Returns <paramref name="text"/> with any credential-shaped substring replaced by
    /// <see cref="Replacement"/>. Null and empty input pass through unchanged.
    ///
    /// <para>arb-mw7: input beyond <see cref="MaxCleanseInputLength"/> (plus a small overscrub
    /// margin — see that constant's remarks) is scrubbed and then the tail past the limit is dropped
    /// entirely, never written. A regex timeout on this text is caught here and degrades to
    /// <see cref="TimeoutPlaceholder"/> for THIS text only (arb-qafw): the caller batches many rows
    /// per transaction, and one row's pathological text must not abort the others.</para>
    /// </summary>
    public static string? Cleanse(string? text) => Cleanse(text, timeoutProbe: null);

    /// <summary>
    /// arb-mw7: test-only overload. <paramref name="timeoutProbe"/>, when supplied, runs in place of
    /// the regex pipeline and may throw <see cref="RegexMatchTimeoutException"/> to force the
    /// fail-closed path deterministically — the same seam shape as
    /// <see cref="SanitizedErrorDescription.Describe(Exception, Func{string, string}?)"/>, for the
    /// same reason: the compiled arms' <c>matchTimeoutMilliseconds</c> cannot be swapped at runtime,
    /// and a mutable static toggle would be the process-global hazard
    /// <c>ProductionProcessGlobalStateTests</c> exists to catch. <c>null</c> on every production call
    /// site.
    ///
    /// <para><c>public</c>, not <c>internal</c>, for the same reason as
    /// <see cref="SanitizedErrorDescription.Describe(Exception, Func{string, string}?)"/>: there is no
    /// <c>InternalsVisibleTo</c> from this project to the test assembly.</para>
    /// </summary>
    public static string? Cleanse(string? text, Func<string, string>? timeoutProbe)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        string truncated;
        bool wasTruncated;
        if (text.Length > MaxCleanseInputLength)
        {
            var scrubLength = NudgeOffSurrogatePair(text, Math.Min(text.Length, MaxCleanseInputLength + OverscrubMargin));
            truncated = text[..scrubLength];
            wasTruncated = true;
        }
        else
        {
            truncated = text;
            wasTruncated = false;
        }

        string scrubbed;
        try
        {
            scrubbed = timeoutProbe is not null
                ? timeoutProbe(truncated)
                : CleanseCore(truncated);
        }
        catch (RegexMatchTimeoutException)
        {
            // Per-row fail-closed (arb-qafw): this text becomes a fixed marker, but the caller's
            // batch transaction still commits the other rows.
            return TimeoutPlaceholder;
        }

        if (!wasTruncated)
        {
            return scrubbed;
        }

        // The overscrub margin was only to let a straddling credential match; the actual persisted
        // text is cut at MaxCleanseInputLength, never the extra margin.
        var cut = NudgeOffSurrogatePair(scrubbed, Math.Min(scrubbed.Length, MaxCleanseInputLength));
        return string.Concat(scrubbed.AsSpan(0, cut), TruncationMarker);
    }

    /// <summary>
    /// arb-59gf: moves <paramref name="cut"/> back by one when it would split a surrogate pair.
    ///
    /// <para>Both slices above count UTF-16 code units, but an astral-plane character (any emoji
    /// past the BMP, and every CJK extension block) occupies TWO of them. Cutting between the high
    /// and low halves leaves a LONE SURROGATE at the end of the persisted text — not a character,
    /// and not encodable as UTF-8, so it round-trips as U+FFFD at best and throws at worst on any
    /// consumer that encodes strictly. The store's own column, the <c>/api/admin/logs</c> JSON
    /// response, and an operator's export all sit downstream of this cut, so the cheapest place to
    /// keep the text well-formed is here.</para>
    ///
    /// <para>Testing the char BEFORE the cut is sufficient and does not need a matching low-surrogate
    /// check: a high surrogate at <c>cut - 1</c> is the only way the boundary can land mid-pair,
    /// because its partner necessarily sits at <c>cut</c> — on the discarded side. Dropping that
    /// orphaned high half costs one character of an already-truncated text.</para>
    /// </summary>
    private static int NudgeOffSurrogatePair(string text, int cut) =>
        cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1]) ? cut - 1 : cut;

    private static string CleanseCore(string text)
    {
        // Webhook URLs run first: their credential lives in the path, and a later pattern could
        // otherwise consume part of the URL and leave the secret segment stranded and unredacted.
        // This arm stays HERE rather than moving to CredentialPatterns: it is Data-only, tied to
        // #57's webhook targets, and the status scrubber has no webhook text to strip.
        text = WebhookUrl().Replace(text, m => m.Groups["prefix"].Value + Replacement);

        // arb-6vf: the shared credential arms, the same implementation the status scrubber runs.
        text = CredentialPatterns.RedactCredentials(text);

        return text;
    }
}
