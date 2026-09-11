using System.Net;
using Arbitarr.Api.Routing;
using Arbitarr.Api.Security;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Api.Admin;

/// <summary>
/// D2: gates every <see cref="Arbitarr.Api.Routing.RouteClassification.AdminMutating"/> endpoint
/// behind an admin credential. Read-only lite routes
/// (<see cref="Arbitarr.Api.Routing.RouteClassification.PublicRead"/>) are never wrapped by this
/// filter and stay ungated, per D2.
///
/// <para><b>#44: THE ADMIN UI NOW HAS A LOGIN, AND THIS FILTER ACCEPTS EITHER CREDENTIAL.</b> The
/// sentence that stood here — "the admin UI itself has no login of its own" — was true until #44
/// and is now the thing that changed, so it is corrected rather than removed: a caller may present
/// an <c>X-Admin-Api-Key</c> header OR a valid session cookie, and this remains the entire auth
/// surface for mutating admin routes. Both resolve to one <see cref="CredentialResolution"/> carrying
/// one <see cref="ApiKeyScope"/>, and this filter makes ONE scope check over whichever answered —
/// the owner ruling's requirement that #44 adopt #58's primitive rather than introduce a second
/// authorization model. Key authentication is NOT removed and must not be: Sonarr, Radarr and every
/// scripted caller cannot complete an interactive login.</para>
///
/// <para><b>#44: THE BYPASS STILL SKIPS THE KEY, NEVER THE LOGIN.</b> The unset-key behaviour below
/// is unchanged, and its meaning is deliberately unchanged too — being on the local network makes a
/// fresh install ADMINISTRABLE, it does not make the caller a logged-in operator. A session can
/// never resolve to <see cref="CredentialResolutionOutcome.NotConfigured"/> (see
/// <see cref="ISessionAuthenticator"/>), so no cookie can reach or reopen that branch.</para>
///
/// Expects the key in an <c>X-Admin-Api-Key</c> request header (never a query string, so it does
/// not end up in access logs or browser history the way the Torznab/Newznab client apikey does).
///
/// <para><b>#58: WHAT A CREDENTIAL NOW IS.</b> Before #58 this filter compared the presented header
/// against the one configured admin key. It no longer holds key material or matching logic at all:
/// it asks <see cref="ICredentialResolver"/> and renders the answer as a status code. There are now
/// two kinds of valid credential behind that call — a named, scoped key from the ApiKeys table, and
/// the pre-#58 shared key, which still works and resolves at full scope — and this filter is
/// deliberately unable to tell them apart, so neither can drift into being special-cased here.</para>
///
/// <para><b>#58: 401 VERSUS 403.</b> A wrong credential and a real credential with insufficient
/// authority are now different answers. 401 means the value matched nothing live; 403 means it
/// matched a live key whose scope does not reach this route. Collapsing them — as the pre-#58 gate
/// necessarily did, having only one scope — makes a read-only key's refusal indistinguishable from
/// a typo, and the operator's next action for those two is opposite: widen the key, versus fix the
/// value.</para>
///
/// UNSET-KEY BEHAVIOUR (#43). The original rule here was absolute: "an unset gate must never be
/// treated as 'no gate needed'", and every unkeyed request got a 503. That rule is now QUALIFIED,
/// not abandoned — an unset gate is treated as "no gate needed" ONLY for a request arriving from
/// the local network, and only until a key is set. The qualification is forced by a deadlock: the
/// key lives solely in the Settings table, and the only route that can write it is itself
/// admin-mutating, so an absolute fail-closed rule made a fresh install permanently unreachable —
/// no operator, on any deployment, could ever set a first key. The bypass is the bootstrap path,
/// and <see cref="AdminSecurityEndpoints"/> is what it exists to make reachable; the moment a key
/// is stored this branch stops running and the gate is absolute again for every source address.
/// #58 widens "a key is stored" to include a named key — minting the first one through the bypass
/// closes it just as setting the shared key does — and does not otherwise touch this behaviour.
/// #58 adds NO second bootstrap path: the local-network bypass remains the only one.
///
/// Trust is decided ONLY from <see cref="ConnectionInfo.RemoteIpAddress"/> — the address of the
/// peer on the other end of the actual socket. <c>X-Forwarded-For</c> and every other request
/// header is deliberately ignored: headers are attacker-controlled unless ForwardedHeaders
/// middleware is running with a known-proxy allow-list, and it is not. A remote caller can
/// therefore claim to be loopback all it likes and still get the 503. A null RemoteIpAddress (no
/// socket peer, as in an in-memory test transport) is likewise untrusted — unknown is not local.
/// </summary>
public sealed class AdminApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Api-Key";

    /// <summary>
    /// #44: the header a cookie-authenticated request must additionally carry.
    ///
    /// <para><b>THIS IS THE CSRF CONTROL, AND IT IS ONLY NEEDED FOR THE COOKIE.</b> A cookie is
    /// attached by the browser automatically, so a form or image on another origin can make the
    /// browser issue an authenticated request. <c>SameSite=Lax</c> already blocks the cross-site
    /// POST case, but it is one flag, enforced by the client, with known gaps (older browsers, and
    /// Lax's own top-level-navigation allowance), and the cost of a second independent control here
    /// is one header on a same-origin SPA that is already sending them.</para>
    ///
    /// <para>It works because a cross-origin caller cannot set a custom header on a form or image
    /// request at all: doing so forces a CORS preflight, and this application defines no CORS
    /// policy, so the preflight is refused and the real request is never sent. The API-KEY path
    /// needs none of this — a key is not ambient, so an attacker's page cannot make the browser
    /// attach one.</para>
    /// </summary>
    public const string SessionRequestHeaderName = "X-Arbitarr-Session";

    private readonly ICredentialResolver _resolver;
    private readonly ISessionAuthenticator _sessionAuthenticator;
    private readonly IApiKeyLastUsedRecorder _lastUsedRecorder;
    private readonly LanPassthroughOptions _lanPassthrough;
    private readonly ILogger<AdminApiKeyFilter> _logger;

    public AdminApiKeyFilter(
        ICredentialResolver resolver,
        ISessionAuthenticator sessionAuthenticator,
        IApiKeyLastUsedRecorder lastUsedRecorder,
        LanPassthroughOptions lanPassthrough,
        ILogger<AdminApiKeyFilter> logger)
    {
        _resolver = resolver;
        _sessionAuthenticator = sessionAuthenticator;
        _lastUsedRecorder = lastUsedRecorder;
        _lanPassthrough = lanPassthrough;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // The scope this route demands, read from endpoint metadata — NEVER inferred from the HTTP
        // verb. A GET under an admin prefix is admin-scoped exactly like a POST is. The metadata is
        // attached by the same RequireAdminApiKey() convention that attaches this filter, so the
        // gate and the scope it enforces cannot be wired apart. The fallback is the STRICTER of the
        // two scopes deliberately: a route that somehow reached this filter without metadata should
        // demand more, not less.
        var requiredScope = context.HttpContext.GetEndpoint()?.GetRequiredApiKeyScope() ?? ApiKeyScope.Admin;

        var presentedKey = context.HttpContext.Request.Headers[HeaderName].ToString();
        var resolution = await _resolver.ResolveAsync(
            presentedKey,
            requiredScope,
            context.HttpContext.RequestAborted);

        // #44: EITHER credential opens this gate, and both resolve to the SAME CredentialResolution
        // carrying the SAME ApiKeyScope, so the switch below makes ONE scope decision over whichever
        // one answered. That is the owner ruling's single-model requirement, expressed as control
        // flow rather than as a comment: there is no second branch here that could authorize a
        // session under different rules, because there is no second outcome type to branch on.
        // SessionAndKeyAuthorizeIdenticallyTests asserts the two are interchangeable at the gate.
        //
        // ORDER. The key is tried first, so a machine caller presenting one is never charged for a
        // session lookup, and a browser that holds both a stale cookie and a valid key is judged on
        // the key. A session is consulted only when the key did not already authorize.
        if (resolution.Outcome is not CredentialResolutionOutcome.Authorized)
        {
            var sessionResolution = await AuthenticateSessionAsync(context.HttpContext, requiredScope);

            // Adopted only when the session actually decided something. A rejected session must not
            // overwrite a NotConfigured key outcome, or presenting any junk cookie on a fresh
            // install would turn #43's bootstrap bypass into a 401 and re-deadlock the install —
            // constraint 1 of the owner ruling, in the one place it could actually be broken.
            if (sessionResolution is { Outcome: not CredentialResolutionOutcome.Rejected })
            {
                resolution = sessionResolution;
            }
        }

        // arb-lan-passthrough (operator request, owner-reviewed; ADR 0012): admit any trusted
        // socket peer as a full-scope operator WITHOUT a session or a key, default on.
        //
        // THIS IS A DELIBERATE REVERSAL of the owner ruling documented in TrustedNetwork and
        // ISessionAuthenticator ("the LAN bypass skips the API key only, never the login"). It is
        // NOT the #43 bootstrap bypass: #43 admits an unkeyed caller only while NO credential
        // exists and closes permanently once a key is set. This branch admits a local caller even
        // when a key AND an account exist, so on a plain-HTTP LAN deployment the human login and
        // the admin key both become optional for anyone who can open a socket from an RFC1918
        // address. Behind a reverse proxy the socket peer is the proxy, so unless ForwardedHeaders
        // is configured this either trusts everyone the proxy forwards or no one — it never reads
        // X-Forwarded-For (TrustedNetwork ignores all headers by design).
        //
        // Runs only after the key and the session have both declined, so a CORRECT key or cookie is
        // still judged first and attributed normally. A presented-but-wrong credential from a
        // trusted peer is still admitted: the peer would be admitted with no credential at all, so a
        // wrong one cannot leave it worse off. That includes a live named key of insufficient scope
        // — on the LAN, passthrough's Admin admission supersedes the key's narrower scope. Remote
        // callers are unaffected in every case and still get the 401/403 below.
        if (_lanPassthrough.Enabled
            && resolution.Outcome is not CredentialResolutionOutcome.Authorized
            && IsTrustedNetwork(context.HttpContext.Connection.RemoteIpAddress))
        {
            _logger.LogWarning(
                "LAN passthrough admitted {Method} {Path} from local-network address {RemoteAddress} " +
                "with no session and no API key. This bypasses the operator login; disable it by " +
                "setting the LAN-passthrough option off if the network is not trusted.",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                context.HttpContext.Connection.RemoteIpAddress);

            return await next(context);
        }

        switch (resolution.Outcome)
        {
            case CredentialResolutionOutcome.NotConfigured:
                return await HandleUnconfiguredAsync(context, next);

            case CredentialResolutionOutcome.Authorized:
                // Attribution (#58): recorded by key id, and the label is what appears in a log —
                // never the value, which this filter deliberately never has in a matched form.
                // KeyId is null for the legacy shared key, which has no row to stamp; see
                // DbCredentialResolver's note on why no synthetic row is invented for it.
                if (resolution.KeyId is { } keyId)
                {
                    _lastUsedRecorder.RecordUsed(keyId);
                }

                return await next(context);

            case CredentialResolutionOutcome.InsufficientScope:
                // Logged, because a scope refusal is an operator configuration mistake rather than
                // an attack, and it is unactionable from the 403 alone: the caller sees only "not
                // allowed", while the operator needs to know WHICH key was too narrow. The label is
                // not a secret; the key value is never logged.
                _logger.LogWarning(
                    "API key '{ApiKeyLabel}' has {ActualScope} scope and was refused {Method} {Path}, which requires {RequiredScope} scope.",
                    resolution.Label,
                    resolution.Scope,
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path,
                    requiredScope);

                // The detail deliberately does not name the key or the scope it would need. The
                // caller is a machine holding a credential it already knows; telling it how much
                // more authority exists to be had is information for an attacker who stole that
                // credential, and the operator gets the actionable form in the log line above.
                return Results.Problem(
                    title: "Insufficient API key scope",
                    detail: "The API key presented is valid but is not permitted to perform this action.",
                    statusCode: StatusCodes.Status403Forbidden);

            default:
                return Results.Unauthorized();
        }
    }

    /// <summary>
    /// #44: resolves the session cookie, if one was presented, to the same
    /// <see cref="CredentialResolution"/> a key resolves to. Returns null when no cookie was sent at
    /// all, so the caller can leave the key's own outcome standing.
    ///
    /// <para><b>THE CSRF REQUIREMENT IS ENFORCED HERE, NOT IN THE AUTHENTICATOR.</b> It is a
    /// property of the TRANSPORT (an ambient browser credential), not of the session itself, and
    /// putting it here keeps <see cref="ISessionAuthenticator"/> answering exactly one question.
    /// A cookie presented without <see cref="SessionRequestHeaderName"/> is treated as no credential
    /// rather than as a bad one: the request is not evidence of an authenticated operator's intent,
    /// and the correct response is the same one an unauthenticated request gets.</para>
    /// </summary>
    private async ValueTask<CredentialResolution?> AuthenticateSessionAsync(
        HttpContext httpContext,
        ApiKeyScope requiredScope)
    {
        if (!httpContext.Request.Cookies.TryGetValue(ISessionAuthenticator.CookieName, out var token)
            || string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (!httpContext.Request.Headers.ContainsKey(SessionRequestHeaderName))
        {
            _logger.LogWarning(
                "A session cookie was presented on {Method} {Path} without the {HeaderName} header and was ignored. " +
                "A same-origin caller sends it; a cross-site request cannot.",
                httpContext.Request.Method,
                httpContext.Request.Path,
                SessionRequestHeaderName);

            return null;
        }

        return await _sessionAuthenticator.AuthenticateAsync(token, requiredScope, httpContext.RequestAborted);
    }

    /// <summary>
    /// #43's bootstrap bypass, reached only when NO credential of any kind exists on this deployment
    /// — neither a named key nor the legacy shared value. See the type doc for the deadlock it
    /// resolves and the reason trust is decided from the socket peer alone.
    /// </summary>
    private async ValueTask<object?> HandleUnconfiguredAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var remoteAddress = context.HttpContext.Connection.RemoteIpAddress;
        if (IsTrustedNetwork(remoteAddress))
        {
            // Warning, not information: running unconfigured indefinitely leaves the admin
            // surface open to the whole local network, and the operator should keep seeing
            // that in the log until they close it by setting a key.
            _logger.LogWarning(
                "No admin API key is configured; allowing {Method} {Path} from local-network address {RemoteAddress}. " +
                "Create one via POST /api/admin/keys, or set the shared key via " +
                "PUT /api/admin/security/admin-key, to close this bootstrap bypass.",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                remoteAddress);

            return await next(context);
        }

        return Results.Problem(
            title: "Admin API key not configured",
            detail: "No admin API key has been set yet. Configure one from a machine on the local " +
                    "network before using admin-mutating endpoints.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Whether <paramref name="address"/> is on a network Arbitarr treats as local for the
    /// unconfigured-key bootstrap bypass.
    ///
    /// <para><b>THE DEFINITION MOVED (#44), AND THIS IS NOW A FORWARDER.</b> The ranges and the
    /// socket-peer-only rule live in <see cref="TrustedNetwork"/>, because #44's first-run account
    /// setup needs the identical predicate and two copies would drift into two trust boundaries.
    /// This member remains as the name #43's tests and readers already know, and so the filter's
    /// call site still reads as a local decision — but there is exactly one definition, over
    /// there. Do not reinline it.</para>
    /// </summary>
    internal static bool IsTrustedNetwork(IPAddress? address) => TrustedNetwork.IsTrusted(address);
}
