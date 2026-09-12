using Arbitarr.Api.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M2-8: every endpoint registered by the real Host must carry an explicit
/// <see cref="RouteClassification"/> — <see cref="RouteClassificationExtensions.WithClassification{TBuilder}"/>
/// has no defaulted overload, so a new endpoint cannot compile without one; this test proves the
/// read-back side by enumerating the live <see cref="EndpointDataSource"/> the app actually serves
/// from and asserting no endpoint is missing the metadata.
/// </summary>
public sealed class RouteClassificationTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public RouteClassificationTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // M2-8 compile-fail proof: WithClassification<TBuilder> has no defaulted overload, so mapping
    // an endpoint without it is a compile error, not a runtime gap the test above has to catch.
    // Uncommenting the line below must fail the build with CS7036 ("required parameter
    // 'classification' has no argument given"):
    //
    //     app.MapGet("/api/uncommitted-endpoint", () => Results.Ok());
    //     // ^ compiles: MapGet alone has no classification requirement.
    //     // The requirement is enforced by never calling WithClassification's parameterless form
    //     // — there isn't one:
    //
    //     app.MapGet("/api/uncommitted-endpoint", () => Results.Ok())
    //         .WithClassification();
    //     // ^ CS7036: no overload for 'WithClassification' takes 0 arguments; the only overload
    //     // requires a RouteClassification argument, so every mapped endpoint must state one
    //     // explicitly to compile.

    [Fact]
    public void Every_mapped_endpoint_carries_an_explicit_route_classification()
    {
        using var client = _factory.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var unclassified = dataSource.Endpoints
            .Where(e => e.GetClassification() is null)
            .Select(e => e.DisplayName)
            .ToArray();

        Assert.True(unclassified.Length == 0,
            $"Endpoint(s) missing an explicit RouteClassification: {string.Join(", ", unclassified)}");
    }

    [Fact]
    public void Dashboard_endpoints_are_classified_PublicRead()
    {
        using var client = _factory.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var dashboardRoutes = new[] { "/api/status", "/api/searches/recent", "/api/config/effective", "/api/system/build" };

        foreach (var route in dashboardRoutes)
        {
            var endpoint = dataSource.Endpoints
                .OfType<RouteEndpoint>()
                .SingleOrDefault(e => e.RoutePattern.RawText == route);

            Assert.NotNull(endpoint);
            Assert.Equal(RouteClassification.PublicRead, endpoint!.GetClassification());
        }
    }

    /// <summary>
    /// arb-6l9b.3: the queue reads are <see cref="RouteClassification.AdminMutating"/> even though
    /// they are GETs.
    ///
    /// <para><b>Classification is by PATH PREFIX, never by verb</b> (CLAUDE.md §2), and this is the
    /// case where the verb heuristic would get it wrong most plausibly: a GET that returns no stored
    /// secret reads like a dashboard fact. It is not one. It makes an AUTHENTICATED OUTBOUND CALL on
    /// the operator's behalf using a stored credential, so exposing it unauthenticated would hand any
    /// LAN caller a view of the operator's download activity and a way to make this process issue
    /// key-bearing requests on demand. Pinned by name because the generic sweep asserts only that
    /// whatever classification a route declares is honoured — it cannot notice a route that declared
    /// the wrong one.</para>
    /// </summary>
    [Fact]
    public void The_arr_queue_reads_are_classified_AdminMutating_despite_being_GETs()
    {
        using var client = _factory.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();

        var queueRoutes = new[]
        {
            Arbitarr.Api.Admin.AdminArrQueueEndpoints.SonarrQueueRoute,
            Arbitarr.Api.Admin.AdminArrQueueEndpoints.RadarrQueueRoute,
        };

        foreach (var route in queueRoutes)
        {
            var endpoint = dataSource.Endpoints
                .OfType<RouteEndpoint>()
                .SingleOrDefault(e => e.RoutePattern.RawText == route);

            Assert.NotNull(endpoint);
            Assert.Equal(RouteClassification.AdminMutating, endpoint!.GetClassification());

            // CONCRETE, NOT TEMPLATED, and asserted rather than assumed:
            // AdminApiKeyRouteEnumerationTests' sweep skips every {-containing route by design, so a
            // later edit moving paging into a path segment would drop these out of that sweep in
            // silence. Paging is query-string precisely to keep this true.
            Assert.DoesNotContain('{', endpoint.RoutePattern.RawText!);
        }
    }
}
