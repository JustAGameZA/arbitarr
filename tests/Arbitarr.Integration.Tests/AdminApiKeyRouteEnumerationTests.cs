using System.Net;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
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
/// <see cref="AdminEndpointConventions.RequireAdminApiKey"/> (e.g. a raw
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

        foreach (var (method, path) in GetRoutesByClassification(RouteClassification.PublicRead))
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);

            Assert.False(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
                $"Expected {method} {path} (classified PublicRead) to be servable without an admin " +
                $"key, but it returned {(int)response.StatusCode} {response.StatusCode} — a lite " +
                "read-only route must never be gated per D2.");
        }
    }

    /// <summary>
    /// Enumerates every concrete (non-templated), classified <see cref="RouteEndpoint"/> the real
    /// Host serves, paired with one HTTP method it actually accepts. Route-templated endpoints
    /// (e.g. <c>/api/admin/settings/{key}</c>) are skipped — they need a real key value to resolve
    /// and are already covered by name in <see cref="AdminSettingsEndpointsTests"/>; this test's
    /// job is the generic "every classified endpoint" sweep, not templated-route resolution.
    /// </summary>
    private IEnumerable<(HttpMethod Method, string Path)> GetRoutesByClassification(RouteClassification classification)
    {
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

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
