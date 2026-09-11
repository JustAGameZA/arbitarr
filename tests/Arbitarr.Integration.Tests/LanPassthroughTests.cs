using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Security;
using Arbitarr.Core.Security;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// ADR 0012 (arb-lan-passthrough): a trusted socket peer is admitted to admin routes with no
/// session and no key, and the whoami surface reports it as signed in, when passthrough is on.
///
/// <para><b>EVERY CASE HERE RUNS ON A CLAIMED INSTANCE</b> — an admin key AND an account exist — so
/// #43's bootstrap bypass is closed and #44's first-run setup is over. That is the only state in
/// which passthrough is the thing doing the admitting: on an unclaimed instance the same 200 would
/// come from the bootstrap bypass and prove nothing about this feature. Each admitting case has
/// its passthrough-OFF twin as the positive control, driven through the identical fixture, so a
/// 200 is attributable to the option and not to a fixture that admits everything.</para>
///
/// <para>The <see cref="RemoteAddressWebApplicationFactory"/> defaults passthrough OFF for exactly
/// the reason its constructor documents; this file is where it is switched ON.</para>
/// </summary>
public sealed class LanPassthroughTests
{
    private const string LegacyKey = "the-real-admin-key";
    private const string Username = "operator";
    private const string Password = "example-operator-passphrase";
    private const string AdminScopeRoute = "/api/admin/ping";

    private static readonly IPAddress RemoteAddress = IPAddress.Parse("192.0.2.10");

    /// <summary>A host with a configured admin key AND a created account, stamped with <paramref name="peer"/>.</summary>
    private static async Task<RemoteAddressWebApplicationFactory> CreateClaimedFactoryAsync(IPAddress peer, bool passthrough)
    {
        var factory = new RemoteAddressWebApplicationFactory(peer, lanPassthrough: passthrough);

        await factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = LegacyKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = LegacyKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });

        // The account is created through the repository rather than POST /api/auth/setup, because
        // setup is LAN-gated and the remote-peer cases could not reach it. One fixture shape for
        // every peer keeps the passthrough-on and passthrough-off twins identical except the option.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.Security.UserRepository>();
            var created = await users.CreateFirstUserAsync(Username, Password, CancellationToken.None);
            Assert.NotNull(created);
        }

        return factory;
    }

    private static HttpClient NoCredentialClient(RemoteAddressWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        return client;
    }

    [Fact]
    public async Task A_local_caller_with_no_credential_is_admitted_to_an_admin_route()
    {
        await using var factory = await CreateClaimedFactoryAsync(IPAddress.Loopback, passthrough: true);
        using var client = NoCredentialClient(factory);

        using var response = await client.PostAsync(AdminScopeRoute, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Passthrough_off_is_the_control_a_local_caller_with_no_credential_is_refused()
    {
        // Same peer, same claimed instance, same route — only the option differs. Without this the
        // 200 above could come from a fixture that never gates anything.
        await using var factory = await CreateClaimedFactoryAsync(IPAddress.Loopback, passthrough: false);
        using var client = NoCredentialClient(factory);

        using var response = await client.PostAsync(AdminScopeRoute, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_remote_caller_with_no_credential_is_still_refused_with_passthrough_on()
    {
        await using var factory = await CreateClaimedFactoryAsync(RemoteAddress, passthrough: true);
        using var client = NoCredentialClient(factory);

        using var response = await client.PostAsync(AdminScopeRoute, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_local_caller_with_a_wrong_key_is_still_admitted()
    {
        // Documented consequence (see AdminApiKeyFilter): the peer would be admitted with nothing,
        // so a wrong credential cannot leave it worse off.
        await using var factory = await CreateClaimedFactoryAsync(IPAddress.Loopback, passthrough: true);
        using var client = NoCredentialClient(factory);
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, "not-" + LegacyKey);

        using var response = await client.PostAsync(AdminScopeRoute, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Whoami_reports_a_local_caller_with_no_cookie_as_signed_in_with_no_username()
    {
        await using var factory = await CreateClaimedFactoryAsync(IPAddress.Loopback, passthrough: true);
        using var client = factory.CreateClient();

        var session = await client.GetFromJsonAsync<SessionResponse>(AuthEndpoints.SessionRoute);

        Assert.NotNull(session);
        Assert.True(session.Authenticated);
        Assert.Null(session.Username);
        Assert.False(session.SetupRequired);
    }

    [Fact]
    public async Task Whoami_control_a_local_caller_with_no_cookie_is_not_signed_in_when_passthrough_is_off()
    {
        await using var factory = await CreateClaimedFactoryAsync(IPAddress.Loopback, passthrough: false);
        using var client = factory.CreateClient();

        var session = await client.GetFromJsonAsync<SessionResponse>(AuthEndpoints.SessionRoute);

        Assert.NotNull(session);
        Assert.False(session.Authenticated);
        Assert.Null(session.Username);
    }

    [Fact]
    public async Task Whoami_still_names_the_operator_when_a_real_session_is_presented()
    {
        // Passthrough is a fallback, not a replacement: a signed-in operator keeps their name.
        await using var factory = await CreateClaimedFactoryAsync(IPAddress.Loopback, passthrough: true);
        using var client = NoCredentialClient(factory);

        using var login = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookie = Assert.Single(
            login.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(ISessionAuthenticator.CookieName + "=", StringComparison.Ordinal));
        var token = setCookie.Split(';', 2)[0].Split('=', 2)[1];
        client.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={token}");

        var session = await client.GetFromJsonAsync<SessionResponse>(AuthEndpoints.SessionRoute);

        Assert.NotNull(session);
        Assert.True(session.Authenticated);
        Assert.Equal(Username, session.Username);
    }

    [Fact]
    public async Task Whoami_on_an_unclaimed_instance_still_reports_setup_required_with_passthrough_on()
    {
        // Setup wins: no account row means nothing to be signed in as, and the SPA must offer
        // account creation rather than an app the caller cannot configure a login for.
        await using var factory = new RemoteAddressWebApplicationFactory(IPAddress.Loopback, lanPassthrough: true);
        using var client = factory.CreateClient();

        var session = await client.GetFromJsonAsync<SessionResponse>(AuthEndpoints.SessionRoute);

        Assert.NotNull(session);
        Assert.False(session.Authenticated);
        Assert.True(session.SetupRequired);
    }
}
