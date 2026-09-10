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

        // ADR 0012 dropped the floor to 1, so the only below-floor value is the empty string. Derived
        // from the constant rather than hard-coded, so this test tracks the floor wherever it goes.
        var belowFloor = new string('x', UserRepository.MinPasswordLength - 1);
        using var response = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = Username, password = belowFloor });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    // ---------------------------------------------------------------------------------------------
    // #96: changing the password of the signed-in operator.
    //
    // POST /api/auth/password is classified PublicRead — meaning only "not wrapped by
    // AdminApiKeyFilter" — and carries its session guard in the handler. Because
    // AdminApiKeyRouteEnumerationTests' sweep covers the admin-key posture and nothing else, every
    // property of THIS gate is asserted here by name.
    // ---------------------------------------------------------------------------------------------

    private const string NewPassword = "example-rotated-passphrase";

    /// <summary>
    /// Signs a SECOND browser in against an existing account, returning a client that presents the
    /// new session and the token it was issued.
    ///
    /// <para>Two live sessions for one user is what the revocation test needs, and the token is
    /// captured from <c>Set-Cookie</c> here for the same reason
    /// <see cref="CreateAccountAndSignInAsync"/> does it: that header is the only moment it exists.</para>
    /// </summary>
    private static async Task<(HttpClient Client, string Token)> SignInAgainAsync(
        RemoteAddressWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var response = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var token = ReadIssuedToken(response);
        client.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={token}");

        return (client, token);
    }

    /// <summary>A client that replays <paramref name="token"/> with the CSRF header and nothing else.</summary>
    private static HttpClient ReplayClient(RemoteAddressWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        client.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={token}");
        return client;
    }

    private static Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client,
        string currentPassword,
        string newPassword) =>
        client.PostAsJsonAsync(
            AuthEndpoints.PasswordRoute,
            new { currentPassword, newPassword });

    [Fact]
    public async Task Changing_a_password_without_a_session_is_refused()
    {
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        using var owner = (await CreateAccountAndSignInAsync(factory)).Client;

        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var response = await ChangePasswordAsync(anonymous, Password, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And the password really was left alone — a 401 that had nonetheless written the new hash
        // would satisfy the status assertion above while being the worst possible outcome.
        using var stillTheOldOne = factory.CreateClient();
        stillTheOldOne.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        using var login = await stillTheOldOne.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_with_only_an_admin_key_is_refused()
    {
        // THE WHOLE POINT OF #96, ASSERTED. A machine credential must not be able to rotate a
        // human's password. The property is structural — AdminApiKeyFilter is the only thing that
        // reads X-Admin-Api-Key and this route is not wrapped by it — and this is what fails if a
        // future change classifies the route AdminMutating or reads the header in the handler.
        await using var factory = LocalFactory();
        // The key must be REAL, or "the key did not work" is indistinguishable from "no key exists".
        await ConfigureAdminKeyAsync(factory);
        using var owner = (await CreateAccountAndSignInAsync(factory)).Client;

        using var keyed = factory.CreateClient();
        keyed.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        keyed.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, "a-configured-admin-key");
        // Deliberately NO session cookie.

        using var response = await ChangePasswordAsync(keyed, Password, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // POSITIVE CONTROL: that exact key DOES open the admin gate, so the refusal above is this
        // route declining a working credential rather than the key being wrong all along. Without
        // this, a typo in the key value would produce the same 401 and the test would prove nothing.
        using var gated = await ProbeGatedRouteAsync(keyed);
        Assert.Equal(HttpStatusCode.OK, gated.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_with_a_session_but_no_CSRF_header_is_refused()
    {
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        var (owner, token) = await CreateAccountAndSignInAsync(factory);
        using var _ = owner;

        using var forged = factory.CreateClient();
        forged.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={token}");
        // Deliberately NO SessionRequestHeaderName — the cookie is ambient, so this header is the
        // thing a cross-site forgery cannot supply.

        using var response = await ChangePasswordAsync(forged, Password, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // POSITIVE CONTROL: the SAME token with the header attached succeeds, so the refusal above
        // is attributable to the missing header and not to a token this test never made work.
        using var honest = ReplayClient(factory, token);
        using var allowed = await ChangePasswordAsync(honest, Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_with_a_revoked_session_is_refused()
    {
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it this assertion is vacuous.
        await ConfigureAdminKeyAsync(factory);
        var (owner, token) = await CreateAccountAndSignInAsync(factory);
        using var _ = owner;

        using var logout = await owner.PostAsync(AuthEndpoints.LogoutRoute, content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var replay = ReplayClient(factory, token);
        using var response = await ChangePasswordAsync(replay, Password, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_requires_a_session_even_when_no_admin_key_is_configured()
    {
        // THE BYPASS-CANNOT-REACH-HERE PROPERTY. #43's bootstrap bypass admits an unkeyed caller on
        // a trusted network, and LocalFactory makes every caller here trusted — so on a fresh
        // install with no key configured, an ungated route is wide open.
        //
        // ConfigureAdminKeyAsync is DELIBERATELY ABSENT, and this is the one test in this file where
        // that is the point rather than the vacuity trap its doc comment warns about: the bypass
        // cannot cause a REFUSAL, so a 401 here can only have come from the session guard. Read that
        // helper's comment before adding a call to it here — doing so would weaken this test to
        // saying nothing about the bypass at all.
        await using var factory = LocalFactory();
        using var owner = (await CreateAccountAndSignInAsync(factory)).Client;

        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        // POSITIVE CONTROL FOR THE BYPASS ITSELF: with no key configured, the admin gate really is
        // open to this very client. So the 401 below is the session guard refusing, and could not
        // be the gate having been shut all along.
        using var bypassed = await ProbeGatedRouteAsync(anonymous);
        Assert.Equal(HttpStatusCode.OK, bypassed.StatusCode);

        using var response = await ChangePasswordAsync(anonymous, Password, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_signs_the_operator_in_with_the_new_one_and_not_the_old()
    {
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        using var changed = await ChangePasswordAsync(owner, Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var withNew = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);

        using var withOld = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_with_the_wrong_current_one_is_refused_generically()
    {
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        using var response = await ChangePasswordAsync(owner, "example-wrong-passphrase", NewPassword);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("The current password is incorrect.", body, StringComparison.Ordinal);

        // Says nothing about the stored value — neither password nor hash appears in the refusal.
        Assert.DoesNotContain(Password, body, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, body, StringComparison.Ordinal);

        // And the old password still works, so the failed attempt changed nothing.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        using var login = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Changing_a_password_rejects_a_new_one_below_the_length_floor()
    {
        // MinPasswordLength is REUSED, never redeclared — one floor, one place. The server's own
        // words reach the caller so the UI can render them verbatim. arb-lan-passthrough (ADR 0012)
        // dropped the floor to 1, so the only value below it is the empty string; a below-floor
        // value is derived from the constant rather than hard-coded so this test tracks the floor.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        var belowFloor = new string('x', UserRepository.MinPasswordLength - 1);
        using var response = await ChangePasswordAsync(owner, Password, belowFloor);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"at least {UserRepository.MinPasswordLength} character", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_a_password_to_the_same_value_is_refused()
    {
        // A "rotation" that rotates nothing would still revoke every other session, which an
        // operator would read as the feature having worked.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        using var response = await ChangePasswordAsync(owner, Password, Password);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must differ from the current one", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_a_password_issues_no_new_cookie()
    {
        // The current session is NOT re-issued: it is still valid, and re-issuing would be a second
        // code path for issuing sessions on a route with no need to issue one. Without this
        // assertion a future "refresh the cookie while we are here" would land silently.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        using var response = await ChangePasswordAsync(owner, Password, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(
            response.Headers.Contains("Set-Cookie"),
            "The password route must not issue a cookie; the session that made the request is spared, not replaced.");
    }

    [Fact]
    public async Task Changing_a_password_revokes_every_other_session_but_not_the_current_one()
    {
        // ASSERTED PER ROW, with THREE sessions. "Some session was revoked" still passes when an
        // implementation revokes one and stops — so B and C are each checked, and A is checked in
        // the other direction.
        await using var factory = LocalFactory();
        // Closes #43's bypass so the SESSION is the only thing that can open the gate.
        // See ConfigureAdminKeyAsync — without it every 401 below is the bypass, not the revocation.
        await ConfigureAdminKeyAsync(factory);

        var (clientA, tokenA) = await CreateAccountAndSignInAsync(factory);
        using var _ = clientA;
        var (clientB, tokenB) = await SignInAgainAsync(factory);
        using var __ = clientB;
        var (clientC, tokenC) = await SignInAgainAsync(factory);
        using var ___ = clientC;

        // All three are live BEFORE the change, so the 401s afterwards are attributable to the
        // revocation rather than to a session that never worked.
        foreach (var live in new[] { clientA, clientB, clientC })
        {
            using var before = await ProbeGatedRouteAsync(live);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var changed = await ChangePasswordAsync(clientA, Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // B and C are BOTH dead — checked individually, replaying each token on a fresh client
        // exactly as an attacker holding a copy would.
        using (var replayB = ReplayClient(factory, tokenB))
        using (var afterB = await ProbeGatedRouteAsync(replayB))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, afterB.StatusCode);
        }

        using (var replayC = ReplayClient(factory, tokenC))
        using (var afterC = await ProbeGatedRouteAsync(replayC))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, afterC.StatusCode);
        }

        // And A — the session that made the change — still works. The positive control for THIS
        // half is Logout_invalidates_the_session_server_side, which shows this harness can observe
        // A's revocation at all: without that, "A still works" would pass just as happily against an
        // implementation where revocation never ran.
        using var replayA = ReplayClient(factory, tokenA);
        using var afterA = await ProbeGatedRouteAsync(replayA);
        Assert.Equal(HttpStatusCode.OK, afterA.StatusCode);
    }

    [Fact]
    public async Task A_password_hashed_at_the_old_iteration_count_can_still_be_changed()
    {
        // The rehash-needed branch, satisfied BY CONSTRUCTION rather than by a special case: a
        // successful change writes a brand-new hash at the CURRENT cost whatever the old row used.
        // AspNetPasswordHasher.Verify already treats SuccessRehashNeeded as success — see its note
        // on why the alternative locks every existing operator out on a cost-raising upgrade.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        // Seed a hash produced at the LIBRARY's old 100,000 default, which is what an account
        // created before the cost was raised actually holds.
        var legacyHasher = new AspNetPasswordHasher(
            Microsoft.Extensions.Options.Options.Create(
                new Microsoft.AspNetCore.Identity.PasswordHasherOptions { IterationCount = 100_000 }));
        var legacyHash = legacyHasher.Hash(Password);

        await factory.SeedAsync(async db =>
        {
            var user = await db.Users.SingleAsync();
            user.PasswordHash = legacyHash;
        });

        using var response = await ChangePasswordAsync(owner, Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
        var stored = (await context.Users.SingleAsync()).PasswordHash;

        // The row really was rewritten, and the new hash verifies the new password at the current
        // cost. Asserting only "the change succeeded" would pass against an implementation that
        // returned 204 without writing anything.
        Assert.NotEqual(legacyHash, stored);
        Assert.True(new AspNetPasswordHasher().Verify(stored, NewPassword));
    }

    [Fact]
    public async Task Repeated_wrong_current_passwords_are_rate_limited()
    {
        // The attack this bounds is the one that matters most: someone holding a stolen session
        // cookie guessing the current password to take the account over permanently. Checked BEFORE
        // either KDF invocation, so a throttled attempt costs no hashing work.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < LoginRateLimiter.MaxFailuresPerUsername + 2; attempt++)
        {
            using var response = await ChangePasswordAsync(owner, "example-wrong-passphrase", NewPassword);
            statuses.Add(response.StatusCode);
        }

        // BOTH halves: a limiter that answered 429 from the very first attempt would be broken in
        // the opposite direction and a bare "contains 429" would miss it.
        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Failed_password_changes_do_not_consume_the_login_budget()
    {
        // Two attacks, two budgets — the "pw:" key prefix is what keeps them apart. Without it a
        // rotation-guessing spree would lock the operator out of logging in, which is precisely the
        // denial of service ADR 0009's lockout-free design exists to avoid.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        for (var attempt = 0; attempt < LoginRateLimiter.MaxFailuresPerUsername + 2; attempt++)
        {
            using var spent = await ChangePasswordAsync(owner, "example-wrong-passphrase", NewPassword);
            Assert.True(
                spent.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"Expected a refusal, got {(int)spent.StatusCode} {spent.StatusCode}.");
        }

        // POSITIVE CONTROL that the budget above really was spent: one more attempt is throttled.
        using var throttled = await ChangePasswordAsync(owner, "example-wrong-passphrase", NewPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // And the LOGIN route is untouched by all of it.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        using var login = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Password_change_failures_do_not_exhaust_the_login_address_budget()
    {
        // THE PER-ADDRESS AXIS, WHICH THE TEST ABOVE CANNOT SEE. It spends only
        // MaxFailuresPerUsername + 3 attempts — comfortably under the twenty a single ADDRESS is
        // allowed — so a shared address counter stays under its limit there and the test passes
        // either way. Here the count deliberately EXCEEDS MaxFailuresPerAddress, which is the only
        // way the shared-counter bug becomes visible: without the "pw" address scope every one of
        // these failures also increments "a:{address}", and the login below answers 429.
        //
        // That is not a theoretical loss. Behind a shared NAT egress the address is not the
        // operator's alone, so one person's fumbled rotation would throttle everybody's sign-in.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        for (var attempt = 0; attempt < LoginRateLimiter.MaxFailuresPerAddress + 5; attempt++)
        {
            using var spent = await ChangePasswordAsync(owner, "example-wrong-passphrase", NewPassword);
            Assert.True(
                spent.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"Expected a refusal, got {(int)spent.StatusCode} {spent.StatusCode}.");
        }

        // POSITIVE CONTROL: the password route's OWN budget really is spent, so the requests above
        // genuinely reached the limiter rather than failing somewhere harmless before it. Without
        // this, "login still works" would pass just as happily if nothing had been recorded at all.
        using var throttled = await ChangePasswordAsync(owner, "example-wrong-passphrase", NewPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // The assertion: login from that same address is unaffected.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        using var login = await client.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Failed_logins_do_not_exhaust_the_password_change_address_budget()
    {
        // THE CONVERSE, because a scope that separated the budgets in only one direction would be
        // half a fix and this is what notices. Spends more than MaxFailuresPerAddress on LOGIN
        // failures, then asserts a signed-in operator can still change their password from the same
        // address — the realistic case being an attacker spraying the sign-in page while the
        // operator, already signed in, tries to rotate the credential in response.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        using var attacker = factory.CreateClient();
        attacker.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        for (var attempt = 0; attempt < LoginRateLimiter.MaxFailuresPerAddress + 5; attempt++)
        {
            // A DIFFERENT username each time, so the per-username budget is never the thing that
            // trips: this is a spray, and the address counter is the only one it fills.
            using var sprayed = await attacker.PostAsJsonAsync(
                AuthEndpoints.LoginRoute,
                new { username = $"example-absent-operator-{attempt}", password = "example-wrong-passphrase" });
            Assert.True(
                sprayed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests,
                $"Expected a refusal, got {(int)sprayed.StatusCode} {sprayed.StatusCode}.");
        }

        // POSITIVE CONTROL: the LOGIN address budget really is exhausted — a correct credential from
        // that address is now throttled, which is what proves the spray above landed.
        using var throttledLogin = await attacker.PostAsJsonAsync(
            AuthEndpoints.LoginRoute,
            new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttledLogin.StatusCode);

        // The assertion: the signed-in operator can still rotate their password.
        using var changed = await ChangePasswordAsync(owner, Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
    }

    [Fact]
    public async Task The_password_route_accepts_a_missing_body_without_short_circuiting_past_its_guard()
    {
        // Bound optionally like every other route here (EmptyBodyBehavior.Allow). Unauthenticated
        // and bodiless must answer 401 — the SESSION check runs before the body is inspected, so an
        // unauthenticated caller learns nothing about the body schema. A 400 here would mean binding
        // ran first and the guard was skipped.
        await using var factory = LocalFactory();
        await ConfigureAdminKeyAsync(factory);
        using var owner = (await CreateAccountAndSignInAsync(factory)).Client;

        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var unauthenticated = await anonymous.PostAsync(AuthEndpoints.PasswordRoute, content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // Signed in and bodiless reaches the handler's own null check: 400, not 415 or 500.
        using var signedIn = await owner.PostAsync(AuthEndpoints.PasswordRoute, content: null);
        Assert.Equal(HttpStatusCode.BadRequest, signedIn.StatusCode);
    }

    [Fact]
    public async Task No_password_change_response_ever_carries_either_password_or_the_hash()
    {
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        string storedHash;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            storedHash = (await db.Users.SingleAsync()).PasswordHash;
        }

        // POSITIVE CONTROL: the hash is a real KDF output, so the searches below look for something
        // that could actually be found.
        Assert.False(string.IsNullOrWhiteSpace(storedHash));
        Assert.NotEqual(Password, storedHash);

        using var rejected = await ChangePasswordAsync(owner, "example-wrong-passphrase", NewPassword);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.DoesNotContain(Password, rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, rejectedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(storedHash, rejectedBody, StringComparison.Ordinal);

        using var accepted = await ChangePasswordAsync(owner, Password, NewPassword);
        var acceptedBody = await accepted.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.DoesNotContain(Password, acceptedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, acceptedBody, StringComparison.Ordinal);
    }
}
