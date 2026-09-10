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
/// <para>This class holds the first-run setup surface. It was split out of
/// <c>AuthEndpointsTests</c> so xunit can run the auth tests in parallel; shared fixtures live in
/// <see cref="AuthEndpointTestSupport"/>.</para>
/// </summary>
public sealed class AuthSetupEndpointsTests
{
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
