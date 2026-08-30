using Arbitarr.Api.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Admin;

/// <summary>
/// Attaches both <see cref="RouteClassification.AdminMutating"/> and <see cref="AdminApiKeyFilter"/>
/// in one call, so a new admin-mutating endpoint cannot be wired with the classification but
/// without the gate (or vice versa) by a future author forgetting one half.
/// </summary>
public static class AdminEndpointConventions
{
    public static TBuilder RequireAdminApiKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithClassification(RouteClassification.AdminMutating);
        builder.AddEndpointFilter<AdminApiKeyFilter>();
        return builder;
    }
}
