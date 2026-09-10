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
/// <para>This class holds the #96 password-change RATE LIMITING and the separation of its budget
/// from the login budget. These tests spend real failure budgets, so each one performs many real
/// KDF verifications and this is the slowest of the auth classes. It was split out of
/// <c>AuthEndpointsTests</c> so xunit can run the auth tests in parallel; shared fixtures live in
/// <see cref="AuthEndpointTestSupport"/>.</para>
/// </summary>
public sealed class AuthPasswordChangeRateLimitTests
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

}
