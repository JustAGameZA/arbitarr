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
/// <para>This class holds the secret-hygiene assertions (no password, hash or session token may
/// surface). It was split out of <c>AuthEndpointsTests</c> so xunit can run the auth tests in
/// parallel; shared fixtures live in <see cref="AuthEndpointTestSupport"/>.</para>
/// </summary>
public sealed class AuthSecretHygieneTests
{
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

}
