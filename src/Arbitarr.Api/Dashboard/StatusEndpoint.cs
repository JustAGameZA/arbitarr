using Arbitarr.Api.Routing;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
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

/// <summary>
/// One outstanding operator-actionable condition, as reported by <c>/api/status</c>'s health block
/// (arb-ln0).
///
/// Health items are <b>process-lifetime</b>: they live in memory only, are cleared by the specific
/// event that proves the condition is over (for a refused download, an actual successful grab from
/// that source), and are lost on restart. <see cref="ObservedSinceUtc"/> therefore means "first
/// observed since this process started", not "when the condition began" — which is why the payload
/// carries it rather than a duration the client would have to interpret as absolute.
///
/// Nothing secret-shaped belongs here: <c>/api/status</c> is <c>RouteClassification.PublicRead</c>
/// and un-gated, so <see cref="Summary"/> is built from configured names and status codes only,
/// never from upstream-supplied text.
/// </summary>
/// <param name="Key">Stable machine-readable identifier for the kind of condition, e.g. "download-refused-redirect".</param>
/// <param name="Severity">How bad it is. "blocking" — the only value at present — means the affected function cannot work at all until an operator acts.</param>
/// <param name="SourceName">The configured source the condition applies to.</param>
/// <param name="Summary">Human-readable description of the condition, safe for an un-gated surface.</param>
/// <param name="ObservedSinceUtc">When the condition was first observed, counting from process start.</param>
/// <param name="LastObservedUtc">When the condition was most recently observed.</param>
public sealed record HealthItem(
    string Key,
    string Severity,
    string SourceName,
    string Summary,
    DateTimeOffset ObservedSinceUtc,
    DateTimeOffset LastObservedUtc);

/// <summary>Overall service status, as reported by <c>/api/status</c>.</summary>
/// <param name="Status">Overall service status, always "ok" at this milestone (the process is up).</param>
/// <param name="Sources">Per-source health.</param>
/// <param name="Worker">Proactive-refresh worker health (M7-7, R20).</param>
/// <param name="Health">Outstanding operator-actionable conditions (arb-ln0); empty when there are none.</param>
public sealed record StatusResponse(
    string Status,
    IReadOnlyList<SourceStatus> Sources,
    WorkerHealthResponse Worker,
    IReadOnlyList<HealthItem> Health);

/// <summary>Maps the read-only <c>GET /api/status</c> endpoint (M2 §2, D1 surface 1).</summary>
public static class StatusEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/status", HandleAsync)
            .WithClassification(RouteClassification.PublicRead);

    /// <summary>
    /// The <see cref="HealthItem.Key"/> a refused-redirect download reports under. Stable so a
    /// client can branch on the kind of condition without parsing the summary prose.
    /// </summary>
    internal const string DownloadRefusedRedirectKey = "download-refused-redirect";

    private static async Task<StatusResponse> HandleAsync(
        SourceHealthRepository healthRepository,
        IRefreshWorkerHealth workerHealth,
        IDownloadRefusalTracker refusalTracker,
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

        // arb-ln0: the tracker's snapshot is already ordered and already empty when nothing is
        // refused, so this projects it one-for-one rather than filtering. Severity is "blocking"
        // because every download from that source fails until the operator changes the setting —
        // the source itself stays healthy, so nothing in the Sources block above says so.
        var healthItems = refusalTracker.Snapshot()
            .Select(refusal => new HealthItem(
                Key: DownloadRefusedRedirectKey,
                Severity: "blocking",
                SourceName: refusal.SourceName,
                Summary: refusal.Reason,
                ObservedSinceUtc: refusal.ObservedSinceUtc,
                LastObservedUtc: refusal.LastObservedUtc))
            .ToArray();

        return new StatusResponse(Status: "ok", Sources: sources, Worker: worker, Health: healthItems);
    }

    private static string ToStateLabel(CircuitState state) => state switch
    {
        CircuitState.Closed => "closed",
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half-open",
        _ => throw new InvalidOperationException($"Unknown circuit breaker state: {state}"),
    };
}
