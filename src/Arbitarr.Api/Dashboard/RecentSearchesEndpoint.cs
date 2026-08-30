using Arbitarr.Api.Routing;
using Arbitarr.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Dashboard;

/// <summary>Maps the read-only <c>GET /api/searches/recent</c> endpoint (M2 §2, D1 surface 2).</summary>
public static class RecentSearchesEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/searches/recent", Handle)
            .WithClassification(RouteClassification.PublicRead);

    private static IReadOnlyList<RecentSearchEntry> Handle(RecentSearchLog log) => log.GetRecent();
}
