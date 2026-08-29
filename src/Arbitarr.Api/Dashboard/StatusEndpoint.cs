using Arbitarr.Api.Routing;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data.CircuitBreaker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Dashboard;

/// <summary>Per-source health, as reported by <c>/api/status</c>.</summary>
/// <param name="SourceName">Name of the upstream source.</param>
/// <param name="State">Circuit breaker state: "closed", "open", or "half-open".</param>
/// <param name="ConsecutiveFailures">Consecutive failure count since the breaker last closed.</param>
/// <param name="LastError">Most recent error message, if any.</param>
public sealed record SourceStatus(string SourceName, string State, int ConsecutiveFailures, string? LastError);

/// <summary>
/// Overall service status, as reported by <c>/api/status</c>. <see cref="WorkerStatus"/> exists
/// from day one so the dashboard shape is stable across milestones (M2 §2): before M3 lands it MUST
/// report <c>"not-implemented"</c> — never a value that reads as healthy — because no worker exists
/// yet and reporting "ok" would hide that fact from the operator this dashboard exists to inform.
/// </summary>
/// <param name="Status">Overall service status, always "ok" at this milestone (the process is up).</param>
/// <param name="Sources">Per-source health.</param>
/// <param name="WorkerStatus">Proactive-refresh worker status. "not-implemented" until M3.</param>
public sealed record StatusResponse(string Status, IReadOnlyList<SourceStatus> Sources, string WorkerStatus);

/// <summary>Maps the read-only <c>GET /api/status</c> endpoint (M2 §2, D1 surface 1).</summary>
public static class StatusEndpoint
{
    /// <summary>
    /// The only value <see cref="StatusResponse.WorkerStatus"/> may take before M3 implements the
    /// proactive refresh worker (M2-7). Once M3 lands, this becomes one of several real states and
    /// "unknown" is reserved for "worker present, state not yet determined".
    /// </summary>
    public const string WorkerNotImplemented = "not-implemented";

    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/status", HandleAsync)
            .WithClassification(RouteClassification.PublicRead);

    private static async Task<StatusResponse> HandleAsync(SourceHealthRepository healthRepository, CancellationToken cancellationToken)
    {
        var snapshots = await healthRepository.LoadAllAsync(cancellationToken);

        var sources = snapshots
            .Select(kvp => new SourceStatus(
                SourceName: kvp.Key,
                State: ToStateLabel(kvp.Value.State),
                ConsecutiveFailures: kvp.Value.ConsecutiveFailures,
                LastError: kvp.Value.LastError))
            .OrderBy(s => s.SourceName, StringComparer.Ordinal)
            .ToArray();

        return new StatusResponse(Status: "ok", Sources: sources, WorkerStatus: WorkerNotImplemented);
    }

    private static string ToStateLabel(CircuitState state) => state switch
    {
        CircuitState.Closed => "closed",
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half-open",
        _ => throw new InvalidOperationException($"Unknown circuit breaker state: {state}"),
    };
}
