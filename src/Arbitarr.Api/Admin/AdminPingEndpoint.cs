using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// Minimal admin-mutating probe endpoint: proves <see cref="AdminApiKeyFilter"/> is wired
/// end-to-end (M7-6). Not itself a real mutation — later M7 steps add the actual settings/rules
/// mutating endpoints behind the same <see cref="AdminEndpointConventions.RequireAdminApiKey"/> gate.
/// </summary>
public static class AdminPingEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/admin/ping", () => Results.Ok(new { status = "ok" }))
            .RequireAdminApiKey();
}
