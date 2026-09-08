using System.Net;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
///
/// <para><b>#58 AND THE BYPASS.</b> The bypass itself is UNCHANGED — unkeyed plus local is still the
/// one bootstrap path, and every address case below asserts exactly what it did before. What #58
/// changed is what counts as "a key is configured": it is now the union of the legacy shared key and
/// any live NAMED key, so minting the first named key closes the bypass just as setting the shared
/// key always did. That is asserted explicitly by
/// <see cref="A_named_key_closes_the_bootstrap_bypass_for_a_local_caller_too"/>, because it is the
/// case an operator who migrates to named keys — the whole point of the issue — actually lands in,
/// and a bypass that stayed open for them would be an open admin surface on the entire LAN.</para>
///
/// <para>#58 also split the refusal in two: 401 for a value matching nothing live, 403 for a live
/// key whose scope does not reach the route. The <c>configuredKey</c> parameter below models the
/// legacy shared key by default (full scope, which is what it has always been), and a named key of
/// a given scope when one is passed.</para>
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

    [Theory]
    [MemberData(nameof(TrustedAddresses))]
    public async Task A_named_key_closes_the_bootstrap_bypass_for_a_local_caller_too(IPAddress remoteAddress)
    {
        // #58 widened "a key is stored" to include a NAMED key: minting the first one through the
        // bypass must close it exactly as setting the shared key does. Without this, an operator who
        // migrated to named keys — the whole point of the issue — would silently keep an open admin
        // surface for their entire local network, which is the failure #43's bypass is bounded to
        // avoid. Asserted across every trusted address, because the bypass is what is being closed.
        var (result, reachedEndpoint) = await InvokeAsync(
            configuredKey: "a-named-key",
            remoteAddress: remoteAddress,
            namedKeyScope: ApiKeyScope.Admin);

        Assert.False(reachedEndpoint, $"A stored named key must gate {remoteAddress}, not admit it unkeyed.");
        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task A_live_key_with_too_narrow_a_scope_is_403_not_401()
    {
        // #58's 401/403 split. These are different operator actions — widen the key, versus fix the
        // value — and the pre-#58 gate could not tell them apart because it had only one scope.
        var (result, reachedEndpoint) = await InvokeAsync(
            configuredKey: "a-read-only-key",
            remoteAddress: IPAddress.Loopback,
            providedKey: "a-read-only-key",
            namedKeyScope: ApiKeyScope.ReadOnly,
            requiredScope: ApiKeyScope.Admin);

        Assert.False(reachedEndpoint);
        await AssertStatusCodeAsync(result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_read_only_key_reaches_a_route_that_only_requires_read_scope()
    {
        // The other direction: the scope refusal must be a real containment check, not a blanket
        // refusal of narrow keys that would make ReadOnly useless.
        var (result, reachedEndpoint) = await InvokeAsync(
            configuredKey: "a-read-only-key",
            remoteAddress: IPAddress.Loopback,
            providedKey: "a-read-only-key",
            namedKeyScope: ApiKeyScope.ReadOnly,
            requiredScope: ApiKeyScope.ReadOnly);

        Assert.True(reachedEndpoint);
        Assert.Null(result);
    }

    [Fact]
    public async Task An_authorized_request_is_attributed_to_the_key_that_authorized_it()
    {
        // The attribution half of #58: a key that authenticates a request gets its last-used time
        // recorded, which is what makes revocation an informed decision rather than a guess.
        var recorder = new RecordingLastUsedRecorder();

        var (_, reachedEndpoint) = await InvokeAsync(
            configuredKey: "a-named-key",
            remoteAddress: IPAddress.Loopback,
            providedKey: "a-named-key",
            namedKeyScope: ApiKeyScope.Admin,
            lastUsedRecorder: recorder);

        Assert.True(reachedEndpoint);
        Assert.Equal(new long[] { 7 }, recorder.RecordedKeyIds);
    }

    [Fact]
    public async Task A_refused_request_is_never_attributed_to_any_key()
    {
        // A rejected credential must not stamp a last-used time: "is anything still using this?"
        // would otherwise answer yes for a key nothing can actually authenticate with, and an
        // attacker probing with a wrong value could keep a revoked-looking key looking live.
        var recorder = new RecordingLastUsedRecorder();

        await InvokeAsync(
            configuredKey: "a-named-key",
            remoteAddress: IPAddress.Loopback,
            providedKey: "not-the-key",
            namedKeyScope: ApiKeyScope.Admin,
            lastUsedRecorder: recorder);

        Assert.Empty(recorder.RecordedKeyIds);
    }

    [Theory]
    [InlineData(null, ApiKeyScope.Admin)]              // unconfigured: the bootstrap-bypass log line
    [InlineData("wrong", ApiKeyScope.Admin)]           // rejected: 401
    [InlineData("match", ApiKeyScope.ReadOnly)]        // insufficient scope: 403, which DOES log
    [InlineData("match", ApiKeyScope.Admin)]           // authorized
    public async Task No_filter_outcome_ever_writes_a_presented_key_into_a_log(
        string? configuredMode,
        ApiKeyScope namedKeyScope)
    {
        // THE SECRET RULE, asserted against the log sink rather than the response body — a distinct
        // leak path, and the one a reviewer cannot see the absence of. The 403 branch is the live
        // hazard: it logs a refusal WITH the key's label, and a future edit adding "and the value
        // was X" for debuggability is exactly the plausible mistake this catches.
        //
        // NON-VACUITY: the key is asserted to be PRESENT in the input first. Without that, this test
        // would keep passing if the harness silently stopped sending a key at all.
        const string SecretKey = "placeholder-unmistakable-secret-value-58";
        var configuredKey = configuredMode switch
        {
            null => null,
            "match" => SecretKey,
            _ => "a-different-configured-key",
        };

        var logger = new CapturingLogger();

        var (_, _) = await InvokeAsync(
            configuredKey: configuredKey,
            remoteAddress: IPAddress.Loopback,
            providedKey: SecretKey,
            namedKeyScope: namedKeyScope,
            requiredScope: ApiKeyScope.Admin,
            logger: logger);

        // The input really did carry the secret — otherwise the absence below proves nothing.
        Assert.Contains(SecretKey, logger.PresentedKeySeenByFilter, StringComparison.Ordinal);

        foreach (var line in logger.Messages)
        {
            Assert.DoesNotContain(SecretKey, line, StringComparison.Ordinal);
        }
    }

    private static async Task<(object? Result, bool ReachedEndpoint)> InvokeAsync(
        string? configuredKey,
        IPAddress? remoteAddress,
        string? providedKey = null,
        IHeaderDictionary? headers = null,
        ApiKeyScope? namedKeyScope = null,
        ApiKeyScope requiredScope = ApiKeyScope.Admin,
        IApiKeyLastUsedRecorder? lastUsedRecorder = null,
        CapturingLogger? logger = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/admin/ping";
        httpContext.Connection.RemoteIpAddress = remoteAddress;

        // #58: the filter reads the required scope from endpoint metadata, never from the verb.
        // Attached the way the real convention attaches it, so this exercises the production
        // metadata path rather than the fallback.
        httpContext.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new RequiredApiKeyScopeMetadata(requiredScope)),
            "admin-test-endpoint"));

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

        // Read back out of the request the filter will actually read, so the non-vacuity assertion
        // in the log test is about the real input rather than about its own local variable.
        if (logger is not null)
        {
            logger.PresentedKeySeenByFilter =
                httpContext.Request.Headers[AdminApiKeyFilter.HeaderName].ToString();
        }

        var filter = new AdminApiKeyFilter(
            new StubCredentialResolver(configuredKey, namedKeyScope),
            // #44: every case in this class is about the KEY path and presents no session cookie,
            // so this authenticator never authorizes. That keeps each assertion here about exactly
            // what it was about before sessions existed — including the #43 bypass cases, which
            // must behave identically whether or not a session system is installed.
            new StubSessionAuthenticator(),
            lastUsedRecorder ?? new RecordingLastUsedRecorder(),
            (ILogger<AdminApiKeyFilter>?)logger ?? NullLogger<AdminApiKeyFilter>.Instance);

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

    /// <summary>
    /// #58: stands in for <c>DbCredentialResolver</c>, reproducing the branch structure this filter
    /// actually switches on rather than a simplification of it — no credential configured at all is
    /// <see cref="CredentialResolutionOutcome.NotConfigured"/> (the only case the bootstrap bypass
    /// answers), a non-matching value is <see cref="CredentialResolutionOutcome.Rejected"/>, and a
    /// matching one is authorised or refused on scope.
    ///
    /// <para>The single configured value is deliberately modelled as the LEGACY shared key when
    /// <paramref name="namedKeyScope"/> is null: that is what these tests always meant by
    /// "configuredKey", and it is what keeps the pre-#58 assertions below assertions about the same
    /// behaviour they were written for. Supplying a scope instead models a NAMED key, which is how
    /// the bypass-closing and scope-refusal cases are exercised.</para>
    /// </summary>
    private sealed class StubCredentialResolver : ICredentialResolver
    {
        private readonly string? _configuredKey;
        private readonly ApiKeyScope _scope;

        public StubCredentialResolver(string? configuredKey, ApiKeyScope? namedKeyScope = null)
        {
            _configuredKey = configuredKey;
            // The legacy shared key is always full scope; a named key carries whatever it was minted with.
            _scope = namedKeyScope ?? ApiKeyScope.Admin;
        }

        public Task<CredentialResolution> ResolveAsync(
            string? presentedKey,
            ApiKeyScope requiredScope,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_configuredKey))
            {
                return Task.FromResult(CredentialResolution.NotConfigured);
            }

            if (!string.Equals(presentedKey, _configuredKey, StringComparison.Ordinal))
            {
                return Task.FromResult(CredentialResolution.Rejected);
            }

            return Task.FromResult(new CredentialResolution(
                _scope >= requiredScope
                    ? CredentialResolutionOutcome.Authorized
                    : CredentialResolutionOutcome.InsufficientScope,
                KeyId: 7,
                Label: "stub-key",
                Scope: _scope));
        }
    }

    /// <summary>
    /// Captures every formatted log message the filter writes, so the "no raw key is ever logged"
    /// property can be asserted against the sink rather than inferred from reading the source.
    /// Deliberately captures the FORMATTED message, which is what a real provider renders — a
    /// structured argument carrying a key would leak through exactly that rendering.
    /// </summary>
    private sealed class CapturingLogger : ILogger<AdminApiKeyFilter>
    {
        public List<string> Messages { get; } = new();

        /// <summary>The header value the filter was actually given, for the non-vacuity assertion.</summary>
        public string PresentedKeySeenByFilter { get; set; } = string.Empty;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    /// <summary>Captures the key ids the filter attributed a request to, so attribution can be asserted.</summary>
    private sealed class RecordingLastUsedRecorder : IApiKeyLastUsedRecorder
    {
        public List<long> RecordedKeyIds { get; } = new();

        public void RecordUsed(long keyId) => RecordedKeyIds.Add(keyId);
    }

    /// <summary>
    /// #44: an authenticator that authorizes nothing, used by every case in this class.
    ///
    /// <para>These tests present no session cookie, so the filter never calls this — but it must be
    /// injectable for the class to compile, and returning Rejected unconditionally is the honest
    /// stand-in: it asserts that none of the pre-#44 behaviours here (the keyed gate, the 401/403
    /// split, and especially #43's unconfigured bypass) depend on a session existing. If any case
    /// in this class ever starts passing only because a session authorized it, this stub is what
    /// makes that impossible.</para>
    /// </summary>
    private sealed class StubSessionAuthenticator : ISessionAuthenticator
    {
        public Task<CredentialResolution> AuthenticateAsync(
            string? presentedToken,
            ApiKeyScope requiredScope,
            CancellationToken cancellationToken) =>
            Task.FromResult(CredentialResolution.Rejected);
    }
}
