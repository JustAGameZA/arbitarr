using System.Net;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Api.Security;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// D2 (M7-5): the specific "route enumeration" half of the acceptance criterion that
/// <see cref="AdminApiKeyGateTests"/> does not cover — that one only exercises the single
/// hardcoded <c>/api/admin/ping</c> route by name, so a new endpoint carrying
/// <see cref="RouteClassification.AdminMutating"/> metadata but wired without
/// <see cref="AdminEndpointConventions.RequireAdminApiKey(Microsoft.AspNetCore.Builder.RouteHandlerBuilder)"/> (e.g. a raw
/// <c>.WithClassification(AdminMutating)</c> call, bypassing the combined convention) would not be
/// caught by any existing test.
///
/// This test instead walks the live <see cref="EndpointDataSource"/> the real Host serves from and
/// asserts, for every registered route, both directions of D2's contract:
///   - every <see cref="RouteClassification.AdminMutating"/> route is actually gated: rejected
///     without the admin key, accepted with the correct one;
///   - every <see cref="RouteClassification.PublicRead"/> route is never gated: accepted with no
///     admin key at all.
/// A future endpoint that is classified but not actually wired to the filter (or vice versa) fails
/// this test without needing to be named here explicitly.
///
/// #43 STRENGTHENING. The local-network bootstrap bypass in <c>AdminApiKeyFilter</c> made this
/// class's central assertion — "an unkeyed request is rejected" — newly ambiguous: an unkeyed
/// request is now rejected only from an untrusted address, and allowed from a local one. Two things
/// keep the sweep meaningful rather than vacuous:
///
///   1. Every sweep here seeds a key first, so it exercises the KEYED gate (401 on a missing or
///      wrong key) and never touches the unconfigured branch at all. That is the property this
///      class has always been about, and the bypass cannot affect it.
///   2. The unconfigured branch is asserted explicitly and in BOTH directions, against a host with
///      a stamped remote address (<see cref="RemoteAddressWebApplicationFactory"/>): from a public
///      address every admin-mutating route still 503s, and from loopback the bypass admits it.
///
/// Without (2) the bypass would be untested across the route surface as a whole; without (1) the
/// sweep could pass merely because the test transport happens to present no remote address. Both
/// were added by #43 — this class was strengthened, not relaxed.
/// </summary>
public sealed class AdminApiKeyRouteEnumerationTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminApiKeyRouteEnumerationTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Every_AdminMutating_route_rejects_requests_without_the_admin_key()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        foreach (var (method, path) in GetRoutesByClassification(RouteClassification.AdminMutating))
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
                $"Expected {method} {path} (classified AdminMutating) to reject a request without " +
                $"the admin key, but it returned {(int)response.StatusCode} {response.StatusCode}.");
        }
    }

    [Fact]
    public async Task Every_AdminMutating_route_accepts_requests_with_the_correct_admin_key()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        foreach (var (method, path) in GetRoutesByClassification(RouteClassification.AdminMutating))
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);

            using var response = await client.SendAsync(request);

            Assert.False(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
                $"Expected {method} {path} (classified AdminMutating) to accept the correct admin " +
                $"key, but it returned {(int)response.StatusCode} {response.StatusCode}.");
        }
    }

    [Fact]
    public async Task Every_PublicRead_route_never_requires_the_admin_key()
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        var routes = GetRoutesByClassification(RouteClassification.PublicRead).ToList();
        Assert.NotEmpty(routes);

        // #96 IS THE ONE DELIBERATE EXCEPTION, and it is listed rather than filtered by a pattern so
        // that adding a second one is a decision somebody has to write down here.
        //
        // POST /api/auth/password is PublicRead because PublicRead means exactly one thing in this
        // codebase — "not wrapped by AdminApiKeyFilter" — and this route must not be wrapped by it:
        // that filter accepts EITHER credential, so an admin key would then be able to rotate a
        // human's password, which is the one thing #96 forbids. It nonetheless answers 401 to the
        // bare request this sweep sends, because it requires a live SESSION. The two facts are not
        // in conflict; the classification names the admin-key gate, and the session gate is a
        // different one that lives in the handler. See AuthEndpoints' type doc.
        var sessionGated = new HashSet<string>(StringComparer.Ordinal)
        {
            AuthEndpoints.PasswordRoute,
        };

        // The exemption must name a route that actually exists, or a rename would silently turn this
        // into a sweep with a dead entry and one fewer route covered.
        Assert.Contains(routes, r => sessionGated.Contains(r.Path));

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            if (sessionGated.Contains(path))
            {
                // Asserted POSITIVELY rather than merely skipped: "this route refuses an
                // unauthenticated caller" is a SECURITY property, so it has to keep holding, not
                // just be tolerated. A skip would let a change that made this route reachable
                // without a session pass this file in silence.
                Assert.True(
                    response.StatusCode is HttpStatusCode.Unauthorized,
                    $"Expected {method} {path} to REFUSE a request carrying neither a session nor an " +
                    $"admin key with 401, but it returned {(int)response.StatusCode} " +
                    $"{response.StatusCode}. This route is session-gated in its handler; if it stops " +
                    "refusing, an unauthenticated caller can rotate the operator's password.");
                continue;
            }

            Assert.False(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
                $"Expected {method} {path} (classified PublicRead) to be servable without an admin " +
                $"key, but it returned {(int)response.StatusCode} {response.StatusCode} — a lite " +
                "read-only route must never be gated per D2.");
        }
    }

    [Fact]
    public async Task Every_AdminMutating_route_still_fails_closed_from_a_remote_address_when_no_key_is_configured()
    {
        // #43: the bypass must not have opened the admin surface to the internet. This is the whole
        // route surface asserted from a public source address (RFC 5737 documentation address) on a
        // host with NO key configured — every one of them must still 503.
        //
        // SENDING NO BODY IS LOAD-BEARING — do not "improve" this sweep by giving each request a
        // valid one. A required request body is model-bound BEFORE endpoint filters run, so a route
        // declaring one short-circuits to 400 without AdminApiKeyFilter ever executing, letting an
        // unauthenticated remote caller tell a malformed body (400) from a well-formed one (503).
        // That is an information leak past the gate, and a bodiless request is exactly what exposes
        // it: the 400 arrives instead of the 503 this asserts. It is how the leak was found in
        // AdminSecurityEndpoints, whose body is therefore bound optionally and null-checked inside
        // the handler. Send valid bodies here and every such route starts returning its real status,
        // the assertion passes, and the guard silently stops guarding.
        await using var factory = new RemoteAddressWebApplicationFactory(IPAddress.Parse("192.0.2.10"));
        using var client = factory.CreateClient();

        var routes = GetRoutesByClassification(factory.Services, RouteClassification.AdminMutating).ToList();
        Assert.NotEmpty(routes);

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            Assert.True(
                response.StatusCode is HttpStatusCode.ServiceUnavailable,
                $"Expected {method} {path} (classified AdminMutating) to fail closed with 503 for an " +
                $"unkeyed request from a public address, but it returned {(int)response.StatusCode} " +
                $"{response.StatusCode}. The bootstrap bypass must never admit a remote caller.");
        }
    }

    [Fact]
    public async Task Every_AdminMutating_route_is_reachable_from_loopback_when_no_key_is_configured()
    {
        // The other direction, and the reason #43 exists: on a fresh install a local operator must
        // be able to reach the admin surface at all, or no key can ever be set. Asserting the
        // absence of 503/401 (rather than a specific success code) keeps this about the gate —
        // an unkeyed POST to a route with no body may legitimately 400 once it is past the filter.
        await using var factory = new RemoteAddressWebApplicationFactory(IPAddress.Loopback);
        using var client = factory.CreateClient();

        var routes = GetRoutesByClassification(factory.Services, RouteClassification.AdminMutating).ToList();
        Assert.NotEmpty(routes);

        // RESTORE IS THE ONE DELIBERATE EXCEPTION, and it is listed rather than filtered by a
        // pattern so that adding a second one is a decision somebody has to write down here.
        //
        // #56 refuses POST /api/admin/restore while no admin key is configured, even from loopback.
        // multipart/form-data is a CORS-simple content type, so during the bootstrap window a page
        // on any site a LAN user visits could auto-submit it cross-origin and replace the
        // configuration database and the release-GUID secret. Every other route on this sweep sets
        // ONE value; restore replaces every credential the instance holds, which no fresh install
        // needs to do before its key is set — so excluding it costs nothing and closes that window.
        // See RestoreBootstrapRefusalTests in Arbitarr.Api.Tests for the refusal itself.
        var bootstrapExempt = new HashSet<string>(StringComparer.Ordinal)
        {
            AdminBackupEndpoints.RestoreRoute,
        };

        // The exemption must name a route that actually exists, or a rename would silently turn this
        // into a sweep with a dead entry and one fewer route covered.
        Assert.Contains(routes, r => bootstrapExempt.Contains(r.Path));

        foreach (var (method, path) in routes)
        {
            if (bootstrapExempt.Contains(path))
            {
                // Asserted positively rather than merely skipped: the exemption is a SECURITY
                // property, so it has to keep holding, not just be tolerated.
                using var exempt = new HttpRequestMessage(method, path);
                using var exemptResponse = await client.SendAsync(exempt);

                Assert.True(
                    exemptResponse.StatusCode is HttpStatusCode.ServiceUnavailable,
                    $"Expected {method} {path} to REFUSE an unkeyed loopback request while no admin " +
                    $"key is configured (it replaces every credential the instance holds), but it " +
                    $"returned {(int)exemptResponse.StatusCode} {exemptResponse.StatusCode}.");
                continue;
            }

            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            Assert.False(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
                $"Expected {method} {path} (classified AdminMutating) to be reachable from loopback " +
                $"while no admin key is configured, but it returned {(int)response.StatusCode} " +
                $"{response.StatusCode}. Without this the fresh-install deadlock returns.");
        }
    }

    /// <summary>
    /// Enumerates every concrete (non-templated), classified <see cref="RouteEndpoint"/> the real
    /// Host serves, paired with one HTTP method it actually accepts. Route-templated endpoints
    /// (e.g. <c>/api/admin/settings/{key}</c>) are skipped — they need a real key value to resolve
    /// and are already covered by name in <see cref="AdminSettingsEndpointsTests"/>; this test's
    /// job is the generic "every classified endpoint" sweep, not templated-route resolution.
    /// </summary>
    private IEnumerable<(HttpMethod Method, string Path)> GetRoutesByClassification(RouteClassification classification) =>
        GetRoutesByClassification(_factory.Services, classification);

    private static IEnumerable<(HttpMethod Method, string Path)> GetRoutesByClassification(
        IServiceProvider services,
        RouteClassification classification)
    {
        var dataSource = services.GetRequiredService<EndpointDataSource>();

        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.GetClassification() != classification)
            {
                continue;
            }

            var rawText = endpoint.RoutePattern.RawText;
            if (string.IsNullOrEmpty(rawText) || rawText.Contains('{', StringComparison.Ordinal))
            {
                continue;
            }

            var httpMethods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            var method = httpMethods is { Count: > 0 } ? new HttpMethod(httpMethods[0]) : HttpMethod.Get;

            yield return (method, rawText);
        }
    }

    // Upsert rather than Add: ArbitarrWebApplicationFactory's SQLite database is shared across
    // every [Fact] in this IClassFixture-scoped test class (Name is the SettingEntry primary key),
    // so a second test seeding the same key would otherwise collide with a unique-constraint
    // violation instead of simply overwriting the prior test's value.
    private async Task SeedAdminKeyAsync()
    {
        await _factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = AdminKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = AdminKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
    }
}
