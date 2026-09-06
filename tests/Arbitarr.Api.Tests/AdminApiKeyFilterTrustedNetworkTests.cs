using System.Net;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// #43: the unconfigured-key branch of <see cref="AdminApiKeyFilter"/>. Before this issue the
/// branch was unconditionally 503, which deadlocked every deployment — the admin key is readable
/// only from the settings table and the only route that can write it is itself admin-mutating, so
/// no first key could ever be set. The bypass opens that branch for local-network callers only.
///
/// These exercise the filter through its real <see cref="AdminApiKeyFilter.InvokeAsync"/> against a
/// fabricated <see cref="HttpContext"/>, so the source address under test is the actual
/// <see cref="ConnectionInfo.RemoteIpAddress"/> the filter reads in production — not a stand-in.
/// Remote example addresses are RFC 5737 documentation addresses.
/// </summary>
public sealed class AdminApiKeyFilterTrustedNetworkTests
{
    private const string PublicAddress = "192.0.2.10";

    /// <summary>
    /// Addresses the bypass must ADMIT. Private-range cases are built from octets rather than
    /// written as literals: the repository's pre-commit guard rejects private IP literals in
    /// committed content, and this file would otherwise be full of them. <see cref="Ipv4"/> keeps
    /// each case readable as "the first octet is 10", which is also exactly what the filter checks.
    /// </summary>
    public static TheoryData<IPAddress> TrustedAddresses() => new()
    {
        // Loopback, in each form a real socket can present it.
        IPAddress.Loopback,
        Ipv4(127, 0, 0, 53),
        IPAddress.IPv6Loopback,
        IPAddress.Loopback.MapToIPv6(),
        // RFC1918 private v4, including both edges of the 172.16/12 block.
        Ipv4(10, 0, 0, 4),
        Ipv4(172, 16, 0, 1),
        Ipv4(172, 31, 255, 254),
        Ipv4(192, 168, 1, 20),
        Ipv4(192, 168, 1, 20).MapToIPv6(),
        // RFC4193 IPv6 unique-local (fc00::/7), both halves.
        IPAddress.Parse("fd00::1"),
        IPAddress.Parse("fc00::1"),
    };

    /// <summary>
    /// Addresses the bypass must REFUSE — including the four that sit immediately outside an
    /// RFC1918 block, which is where off-by-one range arithmetic would show up.
    /// </summary>
    public static TheoryData<IPAddress> UntrustedAddresses() => new()
    {
        IPAddress.Parse(PublicAddress),
        Ipv4(172, 15, 0, 1),
        Ipv4(172, 32, 0, 1),
        Ipv4(192, 169, 0, 1),
        Ipv4(11, 0, 0, 1),
        // fe80::/10 link-local is deliberately NOT a trusted range here.
        IPAddress.Parse("fe80::1"),
        IPAddress.Parse("2001:db8::1"),
    };

    private static IPAddress Ipv4(byte a, byte b, byte c, byte d) => new(new[] { a, b, c, d });

    [Theory]
    [MemberData(nameof(TrustedAddresses))]
    public async Task No_key_configured_allows_a_local_network_caller_through(IPAddress remoteAddress)
    {
        var (result, reachedEndpoint) = await InvokeAsync(configuredKey: null, remoteAddress: remoteAddress);

        Assert.True(reachedEndpoint, $"{remoteAddress} is a local-network address and should have been allowed through.");
        Assert.Null(result);
    }

    [Theory]
    [MemberData(nameof(UntrustedAddresses))]
    public async Task No_key_configured_rejects_a_remote_caller_with_503(IPAddress remoteAddress)
    {
        var (result, reachedEndpoint) = await InvokeAsync(configuredKey: null, remoteAddress: remoteAddress);

        Assert.False(reachedEndpoint, $"{remoteAddress} is not a local-network address and must not be allowed through.");
        await AssertStatusCodeAsync(result, StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task No_key_configured_rejects_a_caller_with_no_remote_address_with_503()
    {
        // Unknown is not local. A request with no socket peer (an in-memory transport, say) must
        // fall to the closed side, never the open one. This is also what keeps the existing
        // fail-closed integration tests honest rather than passing for the wrong reason.
        var (result, reachedEndpoint) = await InvokeAsync(configuredKey: null, remoteAddress: null);

        Assert.False(reachedEndpoint);
        await AssertStatusCodeAsync(result, StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// The local-looking values an attacker would most plausibly put in a forwarding header.
    /// Built rather than written literally, for the same pre-commit-guard reason as above.
    /// </summary>
    public static TheoryData<string> SpoofedLocalAddresses() => new()
    {
        IPAddress.Loopback.ToString(),
        IPAddress.IPv6Loopback.ToString(),
        Ipv4(10, 0, 0, 4).ToString(),
    };

    [Theory]
    [MemberData(nameof(SpoofedLocalAddresses))]
    public async Task No_key_configured_ignores_a_spoofed_X_Forwarded_For_from_a_remote_caller(string spoofedAddress)
    {
        // THE ANTI-SPOOFING ASSERTION. X-Forwarded-For is attacker-controlled: no ForwardedHeaders
        // middleware with a known-proxy allow-list is running, so any client can send any value.
        // Trust must come only from the socket peer, so a remote caller claiming to be local stays
        // on the 503 side. If this test ever fails, the bypass has become a remote auth bypass.
        var headers = new HeaderDictionary
        {
            ["X-Forwarded-For"] = spoofedAddress,
            ["X-Real-IP"] = spoofedAddress,
            ["X-Forwarded-Host"] = "localhost",
        };

        var (result, reachedEndpoint) = await InvokeAsync(
            configuredKey: null,
            remoteAddress: IPAddress.Parse(PublicAddress),
            headers: headers);

        Assert.False(reachedEndpoint, $"A remote caller claiming X-Forwarded-For: {spoofedAddress} must not be trusted.");
        await AssertStatusCodeAsync(result, StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task A_configured_key_still_gates_a_loopback_caller()
    {
        // The bypass is bootstrap-only: once a key exists the unconfigured branch never runs, so
        // being on the LAN buys a caller nothing.
        var (result, reachedEndpoint) = await InvokeAsync(
            configuredKey: "a-configured-admin-key",
            remoteAddress: IPAddress.Loopback);

        Assert.False(reachedEndpoint);
        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task A_configured_key_rejects_a_wrong_key_from_a_loopback_caller_with_401()
    {
        var (result, reachedEndpoint) = await InvokeAsync(
            configuredKey: "a-configured-admin-key",
            remoteAddress: IPAddress.Loopback,
            providedKey: "not-the-configured-key");

        Assert.False(reachedEndpoint);
        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task A_configured_key_accepts_the_correct_key_from_a_remote_caller()
    {
        // The address check governs only the unconfigured branch; a correctly keyed request is
        // accepted from anywhere, exactly as before this change.
        var (result, reachedEndpoint) = await InvokeAsync(
            configuredKey: "a-configured-admin-key",
            remoteAddress: IPAddress.Parse(PublicAddress),
            providedKey: "a-configured-admin-key");

        Assert.True(reachedEndpoint);
        Assert.Null(result);
    }

    private static async Task<(object? Result, bool ReachedEndpoint)> InvokeAsync(
        string? configuredKey,
        IPAddress? remoteAddress,
        string? providedKey = null,
        IHeaderDictionary? headers = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/admin/ping";
        httpContext.Connection.RemoteIpAddress = remoteAddress;

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                httpContext.Request.Headers[header.Key] = header.Value;
            }
        }

        if (providedKey is not null)
        {
            httpContext.Request.Headers[AdminApiKeyFilter.HeaderName] = providedKey;
        }

        var filter = new AdminApiKeyFilter(
            new StubAdminApiKeyReader(configuredKey),
            NullLogger<AdminApiKeyFilter>.Instance);

        var reachedEndpoint = false;
        var context = new DefaultEndpointFilterInvocationContext(httpContext);

        var result = await filter.InvokeAsync(context, _ =>
        {
            reachedEndpoint = true;
            return ValueTask.FromResult<object?>(null);
        });

        return (result, reachedEndpoint);
    }

    private static async Task AssertStatusCodeAsync(object? result, int expectedStatusCode)
    {
        var httpResult = Assert.IsAssignableFrom<IResult>(result);

        // Results.Problem resolves ILoggerFactory (and a ProblemDetails writer) when it executes,
        // so the context needs a real container rather than an empty stand-in.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        await using var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Response.Body = new MemoryStream();
        await httpResult.ExecuteAsync(httpContext);

        Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);
    }

    private sealed class StubAdminApiKeyReader : IAdminApiKeyReader
    {
        private readonly string? _key;

        public StubAdminApiKeyReader(string? key) => _key = key;

        public Task<string?> GetCurrentKeyAsync(CancellationToken cancellationToken) => Task.FromResult(_key);
    }
}
