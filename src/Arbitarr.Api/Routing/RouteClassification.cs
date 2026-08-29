using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Routing;

/// <summary>
/// Whether an endpoint is safe to expose unauthenticated (D2: the lite UI and Torznab/Newznab
/// surface have no built-in auth) or must be gated by the admin API key once M7 wires that filter.
/// </summary>
public enum RouteClassification
{
    /// <summary>Read-only or otherwise safe to expose without authentication on a LAN deployment.</summary>
    PublicRead,

    /// <summary>Mutates state; must be gated by the admin API key (wired at M7).</summary>
    AdminMutating,
}

/// <summary>
/// Requires every mapped endpoint to declare an explicit <see cref="RouteClassification"/> — there
/// is deliberately no overload that defaults it. This is the M2 "route classification" control
/// (plan §M2 step 6): at M7 the admin-key filter attaches to <see cref="RouteClassification.AdminMutating"/>
/// endpoints by construction, so a new endpoint cannot silently ship unauthenticated-but-mutating
/// just because its author forgot to opt in. The M7 route-enumeration test is a second, independent
/// layer that detects the mistake if this wrapper is ever bypassed by calling MapGet/MapPost directly.
/// </summary>
public static class RouteClassificationExtensions
{
    /// <summary>
    /// Attaches the given <paramref name="classification"/> to the endpoint as metadata, so later
    /// middleware (the M7 admin-key filter) can discriminate on it, and so a route-enumeration test
    /// can assert every endpoint carries one.
    /// </summary>
    public static TBuilder WithClassification<TBuilder>(this TBuilder builder, RouteClassification classification)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RouteClassificationMetadata(classification));
        return builder;
    }

    /// <summary>Reads the <see cref="RouteClassification"/> metadata attached to an endpoint, if any.</summary>
    public static RouteClassification? GetClassification(this Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Metadata.GetMetadata<RouteClassificationMetadata>()?.Classification;
    }
}

/// <summary>Endpoint metadata carrying a route's <see cref="RouteClassification"/>.</summary>
public sealed class RouteClassificationMetadata
{
    public RouteClassificationMetadata(RouteClassification classification)
    {
        Classification = classification;
    }

    public RouteClassification Classification { get; }
}
