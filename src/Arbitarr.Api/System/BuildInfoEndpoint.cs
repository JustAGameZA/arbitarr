using Arbitarr.Api.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.SystemInfo;

/// <summary>
/// Maps <c>GET /api/system/build</c> (issue #46 / audit R1): the running build's identity, so
/// "is the box running what I think it's running?" is a glance at the System page instead of an
/// ssh session. PublicRead, not admin-gated -- build identity is not a secret, and gating it would
/// put it behind the admin-key deadlock precisely when it is most needed.
/// </summary>
public static class BuildInfoEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/system/build", Handle)
            .WithClassification(RouteClassification.PublicRead);

    private static BuildInfoResponse Handle(BuildInfo buildInfo, TimeProvider timeProvider) =>
        new(
            CommitSha: buildInfo.CommitSha,
            ImageTag: buildInfo.ImageTag,
            BuildTimestampUtc: buildInfo.BuildTimestampUtc,
            InformationalVersion: buildInfo.InformationalVersion,
            UptimeSeconds: (timeProvider.GetUtcNow() - buildInfo.ProcessStartedUtc).TotalSeconds);
}

/// <summary>Response body of <c>GET /api/system/build</c>.</summary>
public sealed record BuildInfoResponse(
    string CommitSha,
    string ImageTag,
    string BuildTimestampUtc,
    string InformationalVersion,
    double UptimeSeconds);
