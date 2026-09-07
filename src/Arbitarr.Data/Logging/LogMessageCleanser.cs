using System.Text.RegularExpressions;

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
/// misses. Keep it small and cheap; if you find yourself adding a fifth pattern because something
/// leaked, the leak is the bug — fix the call site that logged the secret, then add the pattern.
/// </summary>
public static partial class LogMessageCleanser
{
    /// <summary>The text substituted for a redacted value.</summary>
    public const string Replacement = "<redacted>";

    /// <summary>
    /// A credential carried as a URL query parameter — <c>?apikey=…</c>, <c>&amp;api_key=…</c>,
    /// <c>token</c>, <c>passkey</c>, <c>password</c>. This is the shape most likely to appear here
    /// by accident, because it survives being embedded in an exception's request URI, which is text
    /// no call site deliberately composed.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>[?&](?:api_?key|token|passkey|password)=)(?<value>[^&\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryParameterCredential();

    /// <summary>
    /// An <c>Authorization</c>-style header value, either a scheme-prefixed token
    /// (<c>Bearer …</c>, <c>Basic …</c>) or a <c>key: value</c> / <c>key=value</c> pair whose name
    /// is credential-shaped. Header dumps are the other text an exception drags along unbidden.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b(?:bearer|basic)\s+)(?<value>[A-Za-z0-9+/=._~-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    /// <summary>
    /// A named credential assigned inline: <c>X-Admin-Api-Key: abc…</c>, <c>apikey=abc…</c>,
    /// <c>"password": "abc…"</c>. Deliberately requires a credential-shaped NAME rather than
    /// matching any long token, so ordinary identifiers (a release GUID, a commit SHA) are not
    /// mangled into unreadability — a log line redacted into uselessness is its own outage.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>\b[\w-]*(?:api[_-]?key|apikey|token|passkey|password|secret)[\w-]*""?\s*[:=]\s*""?)(?<value>[^\s,;""'}\]]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedCredential();

    /// <summary>
    /// A webhook URL's secret path segment — Discord and Telegram both put the credential in the
    /// PATH, not a query parameter, so the patterns above cannot see it. #57 will store webhook
    /// targets as secrets "in the same sense as a source API key"; this is here ahead of that so
    /// the sink is not the thing that has to change when #57 lands.
    /// </summary>
    [GeneratedRegex(
        @"(?<prefix>https?://(?:[\w.-]*discord(?:app)?\.com/api/webhooks/|api\.telegram\.org/bot))(?<value>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebhookUrl();

    /// <summary>
    /// Returns <paramref name="text"/> with any credential-shaped substring replaced by
    /// <see cref="Replacement"/>. Null and empty input pass through unchanged.
    /// </summary>
    public static string? Cleanse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Webhook URLs run first: their credential lives in the path, and a later pattern could
        // otherwise consume part of the URL and leave the secret segment stranded and unredacted.
        text = WebhookUrl().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = QueryParameterCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = AuthorizationScheme().Replace(text, m => m.Groups["prefix"].Value + Replacement);
        text = NamedCredential().Replace(text, m => m.Groups["prefix"].Value + Replacement);

        return text;
    }
}
