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
/// <para>This class holds CSRF, logout and the login rate limiter. It was split out of
/// <c>AuthEndpointsTests</c> so xunit can run the auth tests in parallel; shared fixtures live in
/// <see cref="AuthEndpointTestSupport"/>.</para>
/// </summary>
public sealed class AuthCsrfAndLogoutEndpointsTests
{
    [Fact]
    public async Task A_session_cookie_without_the_csrf_header_does_not_authorize()
    {
        // The cookie is ambient — a browser attaches it to a cross-site request automatically — so
        // the custom header is what a forged request cannot supply. Without this, SameSite=Lax
        // would be the only CSRF control, enforced entirely by the client.
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        var (authenticated, cookie) = await CreateAccountAndSignInAsync(factory);
        using var _ = authenticated;

        using var forged = factory.CreateClient();
        forged.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={cookie}");
        // Deliberately NO SessionRequestHeaderName.

        using var response = await ProbeGatedRouteAsync(forged);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_invalidates_the_session_server_side()
    {
        // The property the issue names as missing today: logout must be real, not a discarded
        // cookie. The assertion re-presents the SAME token after logging out, which is exactly what
        // an attacker holding a copy would do — a test that merely checked the cookie was cleared
        // would pass against a purely client-side "logout".
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        var (client, cookie) = await CreateAccountAndSignInAsync(factory);
        using var _ = client;

        using var beforeLogout = await ProbeGatedRouteAsync(client);
        Assert.Equal(HttpStatusCode.OK, beforeLogout.StatusCode);

        using var logout = await client.PostAsync(AuthEndpoints.LogoutRoute, content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var replay = factory.CreateClient();
        replay.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        replay.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={cookie}");

        using var afterLogout = await ProbeGatedRouteAsync(replay);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Repeated_failed_logins_are_rate_limited()
    {
        await using var factory = LocalFactory();
        using var setupClient = (await CreateAccountAndSignInAsync(factory)).Client;

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < LoginRateLimiter.MaxFailuresPerUsername + 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                AuthEndpoints.LoginRoute,
                new { username = Username, password = "example-wrong-passphrase" });
            statuses.Add(response.StatusCode);
        }

        // The first failures are refused as failures; once the budget is spent the answer changes
        // to 429. Asserting BOTH halves matters: a limiter that answered 429 from the very first
        // attempt would be broken in the opposite direction and a "contains 429" test would miss it.
        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task The_correct_password_still_works_before_the_rate_limit_is_reached()
    {
        // Non-vacuity for the test above: proves the limiter is not simply refusing everything.
        await using var factory = LocalFactory();
        using var setupClient = (await CreateAccountAndSignInAsync(factory)).Client;

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var firstFailure = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = "example-wrong-passphrase" });
        Assert.Equal(HttpStatusCode.Unauthorized, firstFailure.StatusCode);

        using var success = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task Auth_routes_accept_a_missing_body_without_short_circuiting_past_their_guards()
    {
        // The bodies are bound optionally (EmptyBodyBehavior.Allow). A REQUIRED body is model-bound
        // before endpoint filters run, and the convention is followed here even though these routes
        // are ungated — so a future change that gates one cannot silently reintroduce the leak.
        // 400/403/409 are all "the handler ran"; 415 or 500 would mean binding rejected it first.
        await using var factory = LocalFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        foreach (var route in new[] { AuthEndpoints.SetupRoute, AuthEndpoints.LoginRoute })
        {
            using var response = await client.PostAsync(route, content: null);

            Assert.True(
                response.StatusCode is HttpStatusCode.BadRequest
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.Conflict
                    or HttpStatusCode.Unauthorized,
                $"Expected {route} to reach its handler with no body, but it returned " +
                $"{(int)response.StatusCode} {response.StatusCode}.");
        }
    }

}
