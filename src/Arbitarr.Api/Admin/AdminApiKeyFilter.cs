using System.Net;
using System.Net.Sockets;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Api.Admin;

/// <summary>
/// D2: gates every <see cref="Arbitarr.Api.Routing.RouteClassification.AdminMutating"/> endpoint
/// behind an admin credential. The admin UI itself has no login of its own — this filter
/// is the entire auth surface for mutating admin routes; read-only lite routes
/// (<see cref="Arbitarr.Api.Routing.RouteClassification.PublicRead"/>) are never wrapped by this
/// filter and stay ungated, per D2.
///
/// Expects the key in an <c>X-Admin-Api-Key</c> request header (never a query string, so it does
/// not end up in access logs or browser history the way the Torznab/Newznab client apikey does).
///
/// <para><b>#58: WHAT A CREDENTIAL NOW IS.</b> Before #58 this filter compared the presented header
/// against the one configured admin key. It no longer holds key material or matching logic at all:
/// it asks <see cref="IAdminKeyResolver"/> and renders the answer as a status code. There are now
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

    private readonly IAdminKeyResolver _resolver;
    private readonly IApiKeyLastUsedRecorder _lastUsedRecorder;
    private readonly ILogger<AdminApiKeyFilter> _logger;

    public AdminApiKeyFilter(
        IAdminKeyResolver resolver,
        IApiKeyLastUsedRecorder lastUsedRecorder,
        ILogger<AdminApiKeyFilter> logger)
    {
        _resolver = resolver;
        _lastUsedRecorder = lastUsedRecorder;
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

        switch (resolution.Outcome)
        {
            case AdminKeyResolutionOutcome.NotConfigured:
                return await HandleUnconfiguredAsync(context, next);

            case AdminKeyResolutionOutcome.Authorized:
                // Attribution (#58): recorded by key id, and the label is what appears in a log —
                // never the value, which this filter deliberately never has in a matched form.
                // KeyId is null for the legacy shared key, which has no row to stamp; see
                // DbAdminKeyResolver's note on why no synthetic row is invented for it.
                if (resolution.KeyId is { } keyId)
                {
                    _lastUsedRecorder.RecordUsed(keyId);
                }

                return await next(context);

            case AdminKeyResolutionOutcome.InsufficientScope:
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
    /// unconfigured-key bootstrap bypass: loopback (v4, v6, and v4-mapped-into-v6), the RFC1918
    /// private v4 ranges (10/8, 172.16/12, 192.168/16), and the RFC4193 IPv6 unique-local range
    /// (fc00::/7). Anything else — including a null address — is remote.
    /// </summary>
    internal static bool IsTrustedNetwork(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        // Unwrap ::ffff:a.b.c.d so a v4 peer on a dual-stack socket is judged by the v4 rules
        // rather than falling through to the v6 arm below.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,
                172 => octets[1] >= 16 && octets[1] <= 31,
                192 => octets[1] == 168,
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 — the top 7 bits are 1111110, so the first octet is 0xfc or 0xfd.
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
