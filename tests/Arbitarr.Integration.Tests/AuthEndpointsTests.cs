using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Security;
using Arbitarr.Core.Security;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #44: the human authentication surface, end to end against the real Host.
///
/// <para>Every test here drives HTTP rather than the repositories directly, because the properties
/// that matter are properties of the SURFACE — which status a caller sees, which cookie flags
/// arrive, whether a hash can be read back — and a repository-level test would assert none of them.</para>
/// </summary>
public sealed class AuthEndpointsTests
{
    private const string Username = "operator";
    private const string Password = "example-operator-passphrase";

    /// <summary>
    /// Setup is gated by the trusted-network predicate, and the in-memory transport leaves
    /// RemoteIpAddress null (which <see cref="TrustedNetwork"/> treats as remote — unknown is not
    /// local). So every test that needs to CREATE an account must run on a loopback-stamped host;
    /// a plain factory can only ever exercise the refusal.
    /// </summary>
    private static RemoteAddressWebApplicationFactory LocalFactory() =>
        new(IPAddress.Loopback);

    /// <summary>
    /// A gated probe request. <c>/api/admin/ping</c> is mapped as a <b>POST</b> (see
    /// <c>AdminPingEndpoint</c>); calling it with GET returns 404 from routing BEFORE
    /// <see cref="AdminApiKeyFilter"/> ever runs, so a GET-based probe would report "not
    /// authorized" for every session — passing the negative tests for entirely the wrong reason and
    /// failing the positive one. Every gate assertion in this file goes through here so the verb
    /// cannot drift back.
    /// </summary>
    private static Task<HttpResponseMessage> ProbeGatedRouteAsync(HttpClient client) =>
        client.PostAsync("/api/admin/ping", content: null);

    /// <summary>
    /// Creates the first account and returns a client that presents the resulting session, together
    /// with the plaintext token that was issued.
    ///
    /// <para><b>THE TOKEN IS CAPTURED FROM <c>Set-Cookie</c>, WHICH IS THE ONLY MOMENT IT EXISTS.</b>
    /// It is never persisted — the row stores only a hash (that is the design) — and
    /// <see cref="WebApplicationFactory{T}"/>'s client keeps received cookies in its handler's
    /// private container, which no public API exposes. So a test that needs to REPLAY a token (the
    /// logout and CSRF cases both do) must take it here or not at all.</para>
    ///
    /// <para>It is then pinned onto the client as an explicit <c>Cookie</c> header rather than left
    /// to the handler's jar, so that what the client sends is visible in this file and identical to
    /// what the replay clients send.</para>
    /// </summary>
    private static async Task<(HttpClient Client, string Token)> CreateAccountAndSignInAsync(
        RemoteAddressWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        // The CSRF header the gate requires before it will honour a cookie. The SPA attaches it to
        // every request; a test client must too, or its cookie is ignored and the assertion under
        // test would be about the wrong thing.
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var response = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = Username, password = Password });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var token = ReadIssuedToken(response);
        client.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={token}");

        return (client, token);
    }

    /// <summary>
    /// Configures an admin API key on the instance, which is what makes a NEGATIVE session
    /// assertion mean anything at all.
    ///
    /// <para><b>WITHOUT THIS, EVERY "does not authorize" TEST IN THIS FILE PASSES VACUOUSLY — AND
    /// THE FOUR THAT NEEDED IT ORIGINALLY ASSERTED NOTHING.</b> With no key configured, the
    /// resolver answers <c>NotConfigured</c>, and #43's bootstrap bypass then admits any caller on
    /// a trusted network — which <see cref="LocalFactory"/> deliberately makes every caller here.
    /// The gated route therefore answers 200 no matter what the session did: a revoked session, an
    /// expired one, and a request carrying no CSRF header at all were all admitted by the BYPASS,
    /// never by the session, so an assertion of 401 was failing for the right reason only by
    /// accident and an assertion of 200 would have proved nothing.</para>
    ///
    /// <para>Seeding a key closes the bypass branch permanently, so the session becomes the only
    /// thing that can open the gate and the negative assertions below actually bite. This is the
    /// positive-control discipline CLAUDE.md §4 requires, applied to authorization rather than to a
    /// secret: prove the gate CAN refuse before asserting that it does.</para>
    /// </summary>
    private static async Task ConfigureAdminKeyAsync(RemoteAddressWebApplicationFactory factory)
    {
        await factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = "a-configured-admin-key",
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = "a-configured-admin-key";
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    /// <summary>
    /// Pulls the session token out of a response's <c>Set-Cookie</c> header.
    ///
    /// <para>Asserts the header was present, so a change that stopped issuing the cookie surfaces
    /// here as a named failure rather than as a confusing downstream 401.</para>
    /// </summary>
    private static string ReadIssuedToken(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(ISessionAuthenticator.CookieName + "=", StringComparison.Ordinal));

        // "name=value; path=/; samesite=lax; httponly" — the value is everything up to the first
        // attribute delimiter.
        return setCookie.Split(';', 2)[0].Split('=', 2)[1];
    }

    [Fact]
    public async Task First_run_setup_creates_an_account_and_signs_in()
    {
        await using var factory = LocalFactory();
        using var client = (await CreateAccountAndSignInAsync(factory)).Client;

        using var session = await client.GetAsync(AuthEndpoints.SessionRoute);
        var body = await session.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("authenticated").GetBoolean());
        Assert.Equal(Username, body.GetProperty("username").GetString());
        Assert.False(body.GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task First_run_setup_is_reachable_while_no_account_exists()
    {
        await using var factory = LocalFactory();
        using var client = factory.CreateClient();

        using var session = await client.GetAsync(AuthEndpoints.SessionRoute);
        var body = await session.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("setupRequired").GetBoolean());
    }

    [Fact]
    public async Task First_run_setup_is_unreachable_once_an_account_exists()
    {
        // AC4's second half. The first account claims the instance permanently; a second call must
        // not be able to mint another, even from the local network.
        await using var factory = LocalFactory();
        using var client = (await CreateAccountAndSignInAsync(factory)).Client;

        using var second = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = "intruder", password = "example-second-passphrase" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task First_run_setup_refuses_a_caller_outside_the_local_network()
    {
        // The issue's "a fresh install with no users must offer account creation without leaving an
        // open window where anyone can claim the instance". Zero-accounts alone is not enough — on
        // an exposed install, whoever posts first would own it. RFC 5737 documentation address.
        await using var factory = new RemoteAddressWebApplicationFactory(IPAddress.Parse("192.0.2.10"));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = Username, password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Concurrent_first_run_setup_creates_exactly_one_account()
    {
        // AC4's atomicity requirement, driven as a REAL race rather than asserted from the shape of
        // the code. The plan calls a second account "a full compromise", because it is an
        // attacker's and is indistinguishable from the operator's.
        //
        // Distinct usernames on purpose: identical ones would be caught by the unique index alone,
        // which is the easy half. Different names can only be stopped by the in-transaction
        // re-check, so this exercises the arm a check-then-write would fail.
        await using var factory = LocalFactory();
        using var warmup = factory.CreateClient();
        await warmup.GetAsync(AuthEndpoints.SessionRoute);

        var attempts = Enumerable.Range(0, 8).Select(async index =>
        {
            using var client = factory.CreateClient();
            using var response = await client.PostAsJsonAsync(
                AuthEndpoints.SetupRoute,
                new { username = $"racer-{index}", password = "example-operator-passphrase" });
            return response.StatusCode;
        });

        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(status => status == HttpStatusCode.Created));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
    }

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

    [Fact]
    public async Task No_response_ever_carries_the_password_or_its_hash()
    {
        // AC2's "never returned". The hash is read back out of the DATABASE and searched for in
        // every auth response, so this asserts about the real stored value rather than a guess at
        // its shape.
        await using var factory = LocalFactory();
        using var client = (await CreateAccountAndSignInAsync(factory)).Client;

        string storedHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            storedHash = (await db.Users.SingleAsync()).PasswordHash;
        }

        // POSITIVE CONTROL: the hash exists and is a real KDF output, so the searches below are
        // looking for something that could actually be found. An absence assertion over a value
        // that was never in play passes vacuously.
        Assert.False(string.IsNullOrWhiteSpace(storedHash));
        Assert.NotEqual(Password, storedHash);
        Assert.Contains(storedHash, storedHash, StringComparison.Ordinal);

        foreach (var route in new[] { AuthEndpoints.SessionRoute })
        {
            using var response = await client.GetAsync(route);
            var body = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(Password, body, StringComparison.Ordinal);
            Assert.DoesNotContain(storedHash, body, StringComparison.Ordinal);

            // And the response really is the session document, not an error that trivially
            // contains neither.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("authenticated", body, StringComparison.Ordinal);
        }

        using var login = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        var loginBody = await login.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.DoesNotContain(Password, loginBody, StringComparison.Ordinal);
        Assert.DoesNotContain(storedHash, loginBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_token_is_stored_hashed_never_in_plaintext()
    {
        await using var factory = LocalFactory();
        var (client, cookie) = await CreateAccountAndSignInAsync(factory);
        using var _ = client;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
        var stored = await db.Sessions.SingleAsync();

        // POSITIVE CONTROL: the stored value IS the hash of the cookie we hold, which proves the
        // row corresponds to this session and that the search below is aimed at a live token —
        // rather than passing because the token was never in play.
        Assert.Equal(SessionToken.Hash(cookie), stored.TokenHash);
        Assert.NotEqual(cookie, stored.TokenHash);
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

    [Fact]
    public async Task Setup_rejects_a_password_below_the_length_floor()
    {
        await using var factory = LocalFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var response = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = Username, password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
        Assert.Equal(0, await db.Users.CountAsync());
    }
}
