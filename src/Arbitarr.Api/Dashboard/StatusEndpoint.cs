using Arbitarr.Api.Routing;
using Arbitarr.Core.Caching;
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
/// Proactive-refresh worker health, as reported by <c>/api/status</c> (M7-7, R20) — a direct
/// projection of <see cref="RefreshWorkerHealth"/>, the real snapshot the worker itself maintains.
/// Replaces the pre-M3 <c>WorkerStatus: "not-implemented"</c> placeholder now that a worker exists.
/// </summary>
/// <param name="Enabled">Whether proactive refresh is turned on.</param>
/// <param name="LastCycleStartedUtc">When the most recent cycle began, or null if no cycle has run yet.</param>
/// <param name="LastCycleCompletedUtc">When the most recent cycle finished, or null if none has completed yet.</param>
/// <param name="LastCycleCandidates">How many refresh candidates the most recent cycle selected.</param>
/// <param name="LastCycleRefreshed">How many of those candidates were successfully refreshed.</param>
/// <param name="LastCycleFailed">How many of those candidates failed to refresh.</param>
/// <param name="LastError">The most recent cycle-level failure's message, or null.</param>
/// <param name="ConsecutiveFailedCycles">How many cycles have faulted in a row.</param>
public sealed record WorkerHealthResponse(
    bool Enabled,
    DateTimeOffset? LastCycleStartedUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    int LastCycleCandidates,
    int LastCycleRefreshed,
    int LastCycleFailed,
    string? LastError,
    int ConsecutiveFailedCycles);

/// <summary>Overall service status, as reported by <c>/api/status</c>.</summary>
/// <param name="Status">Overall service status, always "ok" at this milestone (the process is up).</param>
/// <param name="Sources">Per-source health.</param>
/// <param name="Worker">Proactive-refresh worker health (M7-7, R20).</param>
public sealed record StatusResponse(string Status, IReadOnlyList<SourceStatus> Sources, WorkerHealthResponse Worker);

/// <summary>Maps the read-only <c>GET /api/status</c> endpoint (M2 §2, D1 surface 1).</summary>
public static class StatusEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/status", HandleAsync)
            .WithClassification(RouteClassification.PublicRead);

    private static async Task<StatusResponse> HandleAsync(
        SourceHealthRepository healthRepository,
        IRefreshWorkerHealth workerHealth,
        CancellationToken cancellationToken)
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

        var health = workerHealth.Snapshot;
        var worker = new WorkerHealthResponse(
            Enabled: health.Enabled,
            LastCycleStartedUtc: health.LastCycleStartedUtc,
            LastCycleCompletedUtc: health.LastCycleCompletedUtc,
            LastCycleCandidates: health.LastCycleCandidates,
            LastCycleRefreshed: health.LastCycleRefreshed,
            LastCycleFailed: health.LastCycleFailed,
            LastError: health.LastError,
            ConsecutiveFailedCycles: health.ConsecutiveFailedCycles);

        return new StatusResponse(Status: "ok", Sources: sources, Worker: worker);
    }

    private static string ToStateLabel(CircuitState state) => state switch
    {
        CircuitState.Closed => "closed",
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half-open",
        _ => throw new InvalidOperationException($"Unknown circuit breaker state: {state}"),
    };
}
