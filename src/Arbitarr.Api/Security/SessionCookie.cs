using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Security;

/// <summary>
/// #44: writes and clears the session cookie. One place, because the flags below are the part of
/// this feature most likely to be "tidied" into something that breaks the real deployment.
/// </summary>
public static class SessionCookie
{
    /// <summary>
    /// Attaches the session cookie carrying <paramref name="token"/>.
    ///
    /// <para><b>THE FLAGS, EACH WITH ITS REASON — READ BEFORE CHANGING ANY OF THEM.</b></para>
    ///
    /// <list type="bullet">
    /// <item><b><c>HttpOnly = true</c>.</b> Script cannot read the token, so an XSS bug in the SPA
    /// cannot exfiltrate the session. This is also why the token is NOT handed to the frontend to
    /// hold in Zustand the way the admin key is: the admin key must be readable by script because
    /// script has to put it in a header, and a session token has no such requirement, so it gets
    /// the stronger protection. The frontend never sees this value and has no code that could
    /// place it in <c>localStorage</c>.</item>
    ///
    /// <item><b><c>SameSite = Lax</c>.</b> Blocks the cross-site POST that CSRF depends on, while
    /// still sending the cookie on a top-level navigation — so an operator following a bookmark
    /// into Arbitarr arrives logged in rather than at a login form. <c>Strict</c> would break that
    /// and buy little, since the custom-header requirement in <c>AdminApiKeyFilter</c> is the
    /// second, independent CSRF control.</item>
    ///
    /// <item><b><c>Secure = request.IsHttps</c> — CONDITIONAL, AND THIS IS THE IMPORTANT ONE.</b>
    /// A hard <c>Secure = true</c> is what a reviewer reaches for, and it would SILENTLY BREAK
    /// LOGIN ON THE ONLY DEPLOYMENT THAT EXISTS: Arbitarr runs over plain HTTP on a LAN, and a
    /// browser simply discards a <c>Secure</c> cookie on an insecure origin — no error, no console
    /// warning the operator will see, just a login that appears to succeed and a session that never
    /// arrives. The plan calls this out by name as "precisely the class of failure the project has
    /// hit before". Setting it from <c>IsHttps</c> means the flag is present exactly when it can be
    /// honoured: a TLS-terminating reverse proxy gets a <c>Secure</c> cookie, plain HTTP gets a
    /// working one. The residual risk — a token in cleartext on the LAN — is the same exposure the
    /// admin API key already has on this deployment, and is a property of running HTTP, not
    /// something this flag could fix.</item>
    ///
    /// <item><b><c>Path = "/"</c>.</b> The cookie is presented to the API and to the SPA's own
    /// routes alike.</item>
    ///
    /// <item><b>No <c>Expires</c>/<c>Max-Age</c> — a SESSION cookie.</b> It dies with the browser.
    /// Expiry is enforced server-side from the sessions table regardless (see
    /// <c>SessionRepository</c>), so a client-side lifetime would be decoration an attacker holding
    /// the token would simply ignore; making it a session cookie only means an operator closing
    /// their browser genuinely ends the session on that machine.</item>
    /// </list>
    ///
    /// <para><b>The <c>__Host-</c> prefix is NOT used</b>, for the same reason as <c>Secure</c>: the
    /// prefix requires that attribute, so a <c>__Host-</c> cookie would be rejected outright on
    /// plain HTTP.</para>
    /// </summary>
    public static void Attach(HttpResponse response, string token)
    {
        response.Cookies.Append(ISessionAuthenticator.CookieName, token, BuildOptions(response.HttpContext));
    }

    /// <summary>
    /// Clears the cookie on logout.
    ///
    /// <para>The flags must MATCH those used to set it — a browser deletes a cookie only when the
    /// path and domain agree — which is the second reason both operations live in this one file
    /// rather than being written out at each call site.</para>
    ///
    /// <para>This is only the client half of logout. The session is revoked server-side too, and
    /// that is the half that matters: a caller who kept a copy of the token still cannot use it.
    /// Clearing the cookie without revoking would be the "logout" the issue calls a defect.</para>
    /// </summary>
    public static void Clear(HttpResponse response)
    {
        response.Cookies.Delete(ISessionAuthenticator.CookieName, BuildOptions(response.HttpContext));
    }

    private static CookieOptions BuildOptions(HttpContext httpContext) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = httpContext.Request.IsHttps,
        Path = "/",
    };
}
