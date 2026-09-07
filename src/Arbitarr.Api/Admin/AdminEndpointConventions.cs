using Arbitarr.Api.Routing;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Admin;

/// <summary>
/// Attaches <see cref="RouteClassification.AdminMutating"/>, the required
/// <see cref="ApiKeyScope"/>, and <see cref="AdminApiKeyFilter"/> in one call, so a new admin route
/// cannot be wired with some of those and not the others by a future author forgetting a half.
///
/// <para><b>#58: WHY THE SCOPE DEFAULTS TO <see cref="ApiKeyScope.Admin"/>.</b> The overload taking
/// no scope demands full authority, which is what every route carried before #58 and therefore the
/// only default that changes no existing behaviour. Widening a route to
/// <see cref="ApiKeyScope.ReadOnly"/> is an explicit, per-route decision — never something a route
/// acquires by default, and never something inferred from its HTTP verb. A GET under an admin
/// prefix stays admin-scoped unless someone deliberately said otherwise, because "it only reads" is
/// a statement about what the handler does, not about how sensitive what it reads is.</para>
/// </summary>
public static class AdminEndpointConventions
{
    public static RouteHandlerBuilder RequireAdminApiKey(this RouteHandlerBuilder builder) =>
        builder.RequireAdminApiKey(ApiKeyScope.Admin);

    /// <summary>
    /// Gates the route and declares the scope a presented key must have to pass it. A live key with
    /// a narrower scope gets 403 from <see cref="AdminApiKeyFilter"/>, distinct from the 401 an
    /// unknown or revoked key gets.
    /// </summary>
    public static RouteHandlerBuilder RequireAdminApiKey(this RouteHandlerBuilder builder, ApiKeyScope scope)
    {
        builder.WithClassification(RouteClassification.AdminMutating);
        builder.WithRequiredApiKeyScope(scope);
        builder.AddEndpointFilter<AdminApiKeyFilter>();
        return builder;
    }
}
