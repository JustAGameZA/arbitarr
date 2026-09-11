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
/// Shared fixtures for the #44/#96 authentication tests, which live in several classes so that
/// xunit can run them in parallel (a single class is never split across threads). These helpers
/// were moved here verbatim when <c>AuthEndpointsTests</c> was split; each one's doc comment
/// records a constraint that a future edit would otherwise "tidy" into a runtime bug.
/// </summary>
internal static class AuthEndpointTestSupport
{
    internal const string Username = "operator";
    internal const string Password = "example-operator-passphrase";

    internal const string NewPassword = "example-rotated-passphrase";

    /// <summary>
    /// Setup is gated by the trusted-network predicate, and the in-memory transport leaves
    /// RemoteIpAddress null (which <see cref="TrustedNetwork"/> treats as remote — unknown is not
    /// local). So every test that needs to CREATE an account must run on a loopback-stamped host;
    /// a plain factory can only ever exercise the refusal.
    /// </summary>
    internal static RemoteAddressWebApplicationFactory LocalFactory() =>
        new(IPAddress.Loopback);

    /// <summary>
    /// A gated probe request. <c>/api/admin/ping</c> is mapped as a <b>POST</b> (see
    /// <c>AdminPingEndpoint</c>); calling it with GET returns 404 from routing BEFORE
    /// <see cref="AdminApiKeyFilter"/> ever runs, so a GET-based probe would report "not
    /// authorized" for every session — passing the negative tests for entirely the wrong reason and
    /// failing the positive one. Every gate assertion in this file goes through here so the verb
    /// cannot drift back.
    /// </summary>
    internal static Task<HttpResponseMessage> ProbeGatedRouteAsync(HttpClient client) =>
        client.PostAsync("/api/admin/ping", content: null);

    /// <summary>
    /// Creates the first account and returns a client that presents the resulting session, together
    /// with the plaintext token that was issued.
    ///
    /// <para><b>THE TOKEN IS CAPTURED FROM <c>Set-Cookie</c>, WHICH IS THE ONLY MOMENT IT EXISTS.</b>
    /// It is never persisted — the row stores only a hash (that is the design) — and
    /// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>'s client keeps received cookies in its handler's
    /// private container, which no public API exposes. So a test that needs to REPLAY a token (the
    /// logout and CSRF cases both do) must take it here or not at all.</para>
    ///
    /// <para>It is then pinned onto the client as an explicit <c>Cookie</c> header rather than left
    /// to the handler's jar, so that what the client sends is visible in this file and identical to
    /// what the replay clients send.</para>
    /// </summary>
    internal static async Task<(HttpClient Client, string Token)> CreateAccountAndSignInAsync(
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
    internal static async Task ConfigureAdminKeyAsync(RemoteAddressWebApplicationFactory factory)
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
    internal static string ReadIssuedToken(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(ISessionAuthenticator.CookieName + "=", StringComparison.Ordinal));

        // "name=value; path=/; samesite=lax; httponly" — the value is everything up to the first
        // attribute delimiter.
        return setCookie.Split(';', 2)[0].Split('=', 2)[1];
    }

    /// <summary>
    /// Signs a SECOND browser in against an existing account, returning a client that presents the
    /// new session and the token it was issued.
    ///
    /// <para>Two live sessions for one user is what the revocation test needs, and the token is
    /// captured from <c>Set-Cookie</c> here for the same reason
    /// <see cref="CreateAccountAndSignInAsync"/> does it: that header is the only moment it exists.</para>
    /// </summary>
    internal static async Task<(HttpClient Client, string Token)> SignInAgainAsync(
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
    internal static HttpClient ReplayClient(RemoteAddressWebApplicationFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        client.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={token}");
        return client;
    }

    internal static Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client,
        string currentPassword,
        string newPassword) =>
        client.PostAsJsonAsync(
            AuthEndpoints.PasswordRoute,
            new { currentPassword, newPassword });
}
