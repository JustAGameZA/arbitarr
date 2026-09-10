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
/// <para>This class holds the #96 password-change SEMANTICS: what a permitted change does to the
/// stored credential and to other sessions. It was split out of <c>AuthEndpointsTests</c> so xunit
/// can run the auth tests in parallel; shared fixtures live in
/// <see cref="AuthEndpointTestSupport"/>.</para>
/// </summary>
public sealed class AuthPasswordChangeSemanticsTests
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
        // words reach the caller so the UI can render them verbatim.
        await using var factory = LocalFactory();
        var (owner, _) = await CreateAccountAndSignInAsync(factory);
        using var __ = owner;

        using var response = await ChangePasswordAsync(owner, Password, "short");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"at least {UserRepository.MinPasswordLength} characters", body, StringComparison.Ordinal);
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
