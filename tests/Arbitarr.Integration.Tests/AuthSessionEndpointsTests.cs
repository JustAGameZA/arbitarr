using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Security;
using Arbitarr.Core.Security;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Arbitarr.Integration.Tests.AuthEndpointTestSupport;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #44: the human authentication surface, end to end against the real Host.
///
/// <para>Every test here drives HTTP rather than the repositories directly, because the properties
/// that matter are properties of the SURFACE — which status a caller sees, which cookie flags
/// arrive, whether a hash can be read back — and a repository-level test would assert none of them.</para>
///
/// <para>This class holds sign-in and session lifetime. It was split out of
/// <c>AuthEndpointsTests</c> so xunit can run the auth tests in parallel; shared fixtures live in
/// <see cref="AuthEndpointTestSupport"/>.</para>
/// </summary>
public sealed class AuthSessionEndpointsTests
{
    [Fact]
    public async Task Login_succeeds_with_the_right_password_and_fails_with_the_wrong_one()
    {
        await using var factory = LocalFactory();
        using var setupClient = (await CreateAccountAndSignInAsync(factory)).Client;

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var wrong = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = "example-wrong-passphrase" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var right = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Login_answers_identically_for_an_unknown_user_and_a_wrong_password()
    {
        // The username-oracle property: an attacker must not be able to enumerate accounts by
        // reading the difference between "no such user" and "wrong password".
        await using var factory = LocalFactory();
        using var setupClient = (await CreateAccountAndSignInAsync(factory)).Client;

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var unknownUser = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = "no-such-operator", password = Password });

        using var wrongPassword = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = "example-wrong-passphrase" });

        Assert.Equal(unknownUser.StatusCode, wrongPassword.StatusCode);
        Assert.Equal(
            await unknownUser.Content.ReadAsStringAsync(),
            await wrongPassword.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_session_cookie_is_HttpOnly_and_SameSite_Lax_and_not_Secure_over_plain_http()
    {
        // AC3, and the flag combination most likely to be "corrected" into breaking the real
        // deployment. Secure MUST be absent here: the request arrived over plain HTTP (as the LAN
        // deployment does), and a browser silently DISCARDS a Secure cookie on an insecure origin —
        // login would appear to succeed and no session would ever arrive.
        await using var factory = LocalFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var response = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = Username, password = Password });

        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(ISessionAuthenticator.CookieName, StringComparison.Ordinal));

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_session_authorizes_a_gated_route()
    {
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        using var client = (await CreateAccountAndSignInAsync(factory)).Client;

        using var response = await ProbeGatedRouteAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_session_does_not_authorize()
    {
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        using var client = (await CreateAccountAndSignInAsync(factory)).Client;

        using var beforeExpiry = await ProbeGatedRouteAsync(client);
        Assert.Equal(HttpStatusCode.OK, beforeExpiry.StatusCode);

        // Expired by moving the ROW's absolute expiry into the past rather than by moving a clock,
        // because the expiry check reads this column: a session past AbsoluteExpiresAt must be
        // refused even though nothing has pruned it yet. Storage cleanup is not the boundary.
        await factory.SeedAsync(async db =>
        {
            foreach (var session in await db.Sessions.ToListAsync())
            {
                session.AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }
        });

        using var afterExpiry = await ProbeGatedRouteAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, afterExpiry.StatusCode);
    }

    [Fact]
    public async Task An_idle_session_does_not_authorize()
    {
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        using var client = (await CreateAccountAndSignInAsync(factory)).Client;

        // Idle expiry is measured from LastSeenAt against the configured window; pushing it far
        // enough back exceeds any configured idle timeout without touching the absolute one, so
        // this test fails if only the absolute bound is enforced.
        await factory.SeedAsync(async db =>
        {
            foreach (var session in await db.Sessions.ToListAsync())
            {
                session.LastSeenAt = DateTimeOffset.UtcNow.AddDays(-3650);
            }
        });

        using var response = await ProbeGatedRouteAsync(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}
