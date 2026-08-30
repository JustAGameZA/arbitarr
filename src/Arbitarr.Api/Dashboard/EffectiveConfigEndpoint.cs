using Arbitarr.Api.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Dashboard;

/// <summary>Maps the read-only <c>GET /api/config/effective</c> endpoint (M2 §2, D1 surface 3).</summary>
public static class EffectiveConfigEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/config/effective", HandleAsync)
            .WithClassification(RouteClassification.PublicRead);

    private static async Task<EffectiveConfigResponse> HandleAsync(
        EffectiveSettingsReader settingsReader,
        NzbHydraConfigurationStatus nzbHydraStatus,
        CancellationToken cancellationToken)
    {
        var settings = await settingsReader.LoadAsync(cancellationToken);

        return ConfigProjection.Project(settings, nzbHydraStatus.IsConfigured);
    }
}
