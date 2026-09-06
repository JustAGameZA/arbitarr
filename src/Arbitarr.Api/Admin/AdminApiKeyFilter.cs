using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Api.Admin;

/// <summary>
/// D2: gates every <see cref="Arbitarr.Api.Routing.RouteClassification.AdminMutating"/> endpoint
/// behind <c>SettingKey.AdminApiKey</c>. The admin UI itself has no login of its own — this filter
/// is the entire auth surface for mutating admin routes; read-only lite routes
/// (<see cref="Arbitarr.Api.Routing.RouteClassification.PublicRead"/>) are never wrapped by this
/// filter and stay ungated, per D2.
///
/// Expects the key in an <c>X-Admin-Api-Key</c> request header (never a query string, so it does
/// not end up in access logs or browser history the way the Torznab/Newznab client apikey does).
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

    private readonly IAdminApiKeyReader _keyReader;
    private readonly ILogger<AdminApiKeyFilter> _logger;

    public AdminApiKeyFilter(IAdminApiKeyReader keyReader, ILogger<AdminApiKeyFilter> logger)
    {
        _keyReader = keyReader;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredKey = await _keyReader.GetCurrentKeyAsync(context.HttpContext.RequestAborted);
        if (string.IsNullOrEmpty(configuredKey))
        {
            var remoteAddress = context.HttpContext.Connection.RemoteIpAddress;
            if (IsTrustedNetwork(remoteAddress))
            {
                // Warning, not information: running unconfigured indefinitely leaves the admin
                // surface open to the whole local network, and the operator should keep seeing
                // that in the log until they close it by setting a key.
                _logger.LogWarning(
                    "No admin API key is configured; allowing {Method} {Path} from local-network address {RemoteAddress}. " +
                    "Set a key via PUT /api/admin/security/admin-key to close this bootstrap bypass.",
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

        var providedKey = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!IsMatch(providedKey, configuredKey))
        {
            return Results.Unauthorized();
        }

        return await next(context);
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

    // Fixed-time comparison (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals),
    // matching the convention already established by ConfiguredClientApiKeyResolver, so response
    // timing cannot be used to narrow down the admin key.
    private static bool IsMatch(string providedKey, string configuredKey)
    {
        if (string.IsNullOrEmpty(providedKey))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        return providedBytes.Length == configuredBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }
}
