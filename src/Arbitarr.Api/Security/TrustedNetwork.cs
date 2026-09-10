using System.Net;
using System.Net.Sockets;

namespace Arbitarr.Api.Security;

/// <summary>
/// Whether a request's peer is on a network Arbitarr treats as local.
///
/// <para><b>EXTRACTED, NOT COPIED (#44).</b> This logic was #43's, living inside
/// <c>AdminApiKeyFilter</c>. #44 needs the identical predicate to gate first-run account setup, and
/// a second copy would be the beginning of two trust boundaries that drift — one widened, one not,
/// with no test that notices. It therefore MOVED here and the filter now calls it. There is exactly
/// one definition of "local" in this codebase and both callers must keep sharing it.</para>
///
/// <para><b>TRUST IS DECIDED ONLY FROM THE SOCKET PEER</b>
/// (<see cref="Microsoft.AspNetCore.Http.ConnectionInfo.RemoteIpAddress"/>) — the address on the
/// other end of the actual connection. <c>X-Forwarded-For</c> and every other request header is
/// deliberately ignored: headers are attacker-controlled unless ForwardedHeaders middleware is
/// running with a known-proxy allow-list, and it is not. A remote caller can therefore claim to be
/// loopback all it likes and still be refused. A null address (no socket peer, as in an in-memory
/// test transport) is likewise untrusted — unknown is not local.</para>
///
/// <para><b>WHAT THIS IS AND IS NOT AUTHORITY FOR.</b> Being on the LAN is not, in itself,
/// being authenticated. It gated two bootstrap paths: #43's unkeyed-admin bypass while no API key
/// exists, and #44's first-account creation while no account exists — both conditions that stop
/// applying the moment the thing they bootstrap exists.</para>
///
/// <para><b>THE THIRD CALLER NOW EXISTS (ADR 0012, arb-lan-passthrough).</b> The rule here used to
/// be "do not add a third caller that treats a local address as a logged-in operator — that is
/// precisely what the owner ruling on #44 forbids." At the operator's explicit request, and with
/// owner review, <c>AdminApiKeyFilter</c>'s LAN-passthrough branch is exactly that third caller: a
/// trusted socket peer is admitted as a full-scope operator with no session and no key, default on.
/// This predicate is unchanged and still header-blind; ADR 0012 carries the decision, its default,
/// and the reverse-proxy caveat.</para>
/// </summary>
public static class TrustedNetwork
{
    /// <summary>
    /// Whether <paramref name="address"/> is local: loopback (v4, v6, and v4-mapped-into-v6), the
    /// RFC1918 private v4 ranges (10/8, 172.16/12, 192.168/16), and the RFC4193 IPv6 unique-local
    /// range (fc00::/7). Anything else — including a null address — is remote.
    /// </summary>
    public static bool IsTrusted(IPAddress? address)
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
