namespace Arbitarr.Core.Security;

/// <summary>
/// #44: resolves a presented session cookie to the SAME <see cref="AdminKeyResolution"/> a
/// presented API key resolves to.
///
/// <para><b>THIS RETURN TYPE IS THE WHOLE POINT.</b> The owner ruling on #44 is that #58 defines
/// the authorization primitive and #44 adopts it: "do not introduce a second authorization model;
/// that is the exact failure this pair exists to prevent." A session therefore does not get its own
/// outcome enum, its own scope vocabulary, or its own permission table. It resolves to
/// <see cref="AdminKeyResolution"/> carrying an <see cref="ApiKeyScope"/>, and
/// <c>AdminApiKeyFilter</c> makes ONE scope comparison over whichever credential answered. Change
/// this signature to return a session-shaped type and the single-model property is gone —
/// <c>SessionAndKeyAuthorizeIdenticallyTests</c> asserts it is not.</para>
///
/// <para><b>WHAT SCOPE A SESSION CARRIES.</b> <see cref="ApiKeyScope.Admin"/>, always. Arbitarr is
/// a single-operator appliance: there is one role, and a person who logged in is that operator. The
/// scope field is not decoration — it exists so that when a second role does appear, it arrives as
/// a value in the EXISTING vocabulary rather than as a parallel system, which is precisely what the
/// ruling forbids. There is deliberately no per-user scope column yet, because a column with one
/// possible value is a guess about a future requirement rather than a requirement.</para>
///
/// <para><b>NOT A REPLACEMENT FOR <see cref="IAdminKeyResolver"/>.</b> Both are consulted, key
/// first. #44 changes how HUMANS authenticate; it does not remove key authentication (plan §3.5),
/// because Sonarr, Radarr and every scripted caller cannot complete an interactive login.</para>
/// </summary>
public interface ISessionAuthenticator
{
    /// <summary>
    /// The cookie a session token travels in. <c>__Host-</c> is deliberately NOT used: that prefix
    /// requires the <c>Secure</c> attribute, and the real deployment is plain HTTP on a LAN, so a
    /// <c>__Host-</c> cookie would be rejected by the browser and login would fail on the only
    /// deployment that exists. See <c>SessionCookie</c> for the full flag rationale.
    /// </summary>
    public const string CookieName = "arbitarr_session";

    /// <summary>
    /// Resolves <paramref name="presentedToken"/> against the required
    /// <paramref name="requiredScope"/>.
    /// </summary>
    /// <param name="presentedToken">
    /// The raw cookie value, which may be null or empty. An absent, unknown, revoked, or expired
    /// token resolves to <see cref="AdminKeyResolutionOutcome.Rejected"/> —
    /// never to <see cref="AdminKeyResolutionOutcome.NotConfigured"/>, which means something
    /// specific about the API-key gate (no credential exists on this deployment at all) and is what
    /// #43's bootstrap bypass keys off. A session must never be able to produce that outcome, or
    /// presenting a junk cookie would reopen the bypass. That is constraint 1 of the owner ruling —
    /// the LAN bypass skips the API key only, never the login — expressed as a type-level rule.
    /// </param>
    /// <param name="requiredScope">The scope the route being called demands.</param>
    Task<AdminKeyResolution> AuthenticateAsync(
        string? presentedToken,
        ApiKeyScope requiredScope,
        CancellationToken cancellationToken);
}
