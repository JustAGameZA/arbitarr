namespace Arbitarr.Api.Security;

/// <summary>
/// arb-lan-passthrough (operator request, owner-reviewed; ADR 0012). Whether a request arriving
/// from a trusted local-network socket peer is admitted to admin-mutating routes as a full-scope
/// operator WITHOUT a session cookie or an API key.
///
/// <para><b>THIS IS A SECURITY REVERSAL, ON BY DEFAULT.</b> It contradicts the owner ruling
/// recorded in <see cref="TrustedNetwork"/> and <see cref="Arbitarr.Core.Security.ISessionAuthenticator"/>
/// ("the LAN bypass skips the API key only, never the login"). With it on, the human login and the
/// admin key are both optional for any RFC1918 socket peer on a plain-HTTP LAN. It defaults to
/// <see langword="true"/> because the operator asked for that explicitly; the ADR carries the
/// decision and the reverse-proxy caveat (behind a proxy the socket peer is the proxy's address).</para>
///
/// <para>Kept as a single injected object rather than a Settings row so the default is a code
/// constant an operator cannot silently be given a different value for on upgrade, and so a future
/// UI toggle can wrap it without moving the branch in the gate. The environment variable
/// <c>ARBITARR_LAN_PASSTHROUGH</c> set to <c>false</c>/<c>0</c>/<c>off</c> turns it off at startup.</para>
/// </summary>
public sealed class LanPassthroughOptions
{
    public const string EnvironmentVariable = "ARBITARR_LAN_PASSTHROUGH";

    /// <summary>Whether LAN passthrough is active. Defaults to on.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Reads <see cref="EnvironmentVariable"/> and returns the options. Any value other than a
    /// recognised false-y token (<c>false</c>, <c>0</c>, <c>no</c>, <c>off</c>, case-insensitive)
    /// leaves passthrough on, so the default survives a typo rather than failing closed silently —
    /// the operator asked for on, and a surprising off is worse than a surprising on for this
    /// feature's stated purpose.
    /// </summary>
    public static LanPassthroughOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
        var disabled = raw is not null && raw.ToLowerInvariant() is "false" or "0" or "no" or "off";
        return new LanPassthroughOptions { Enabled = !disabled };
    }
}
