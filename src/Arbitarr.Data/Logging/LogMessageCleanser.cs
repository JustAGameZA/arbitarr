using System.Text.RegularExpressions;
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
        // This arm stays HERE rather than moving to CredentialPatterns: it is Data-only, tied to
        // #57's webhook targets, and the status scrubber has no webhook text to strip.
        text = WebhookUrl().Replace(text, m => m.Groups["prefix"].Value + Replacement);

        // arb-6vf: the shared credential arms, the same implementation the status scrubber runs.
        text = CredentialPatterns.RedactCredentials(text);

        return text;
    }
}
