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
/// <para>This class holds the #96 password-change GATE: who may call the route at all. It was
/// split out of <c>AuthEndpointsTests</c> so xunit can run the auth tests in parallel; shared
/// fixtures live in <see cref="AuthEndpointTestSupport"/>.</para>
/// </summary>
public sealed class AuthPasswordChangeGateTests
{
    // ---------------------------------------------------------------------------------------------
    // #96: changing the password of the signed-in operator.
    //
    // POST /api/auth/password is classified PublicRead — meaning only "not wrapped by
    // AdminApiKeyFilter" — and carries its session guard in the handler. Because
    // AdminApiKeyRouteEnumerationTests' sweep covers the admin-key posture and nothing else, every
    // property of THIS gate is asserted here by name.
    // ---------------------------------------------------------------------------------------------

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

}
