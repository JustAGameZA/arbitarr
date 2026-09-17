using Arbitarr.Core.Diagnostics;

namespace Arbitarr.Core.Sources.CircuitBreaker;

/// <summary>
/// Point-in-time state for one source's circuit breaker, as tracked by
/// <see cref="SourceCircuitBreaker"/>. Deliberately shaped to map cleanly onto
/// <c>Arbitarr.Data.Entities.SourceHealthRecord</c> so the persistence adapter is a thin
/// translation, but this type itself has no dependency on Arbitarr.Data.
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="ConsecutiveFailures">Consecutive failure count observed since the breaker last closed.</param>
/// <param name="BaseBackoff">
/// Un-jittered doubling-curve base value (capped at <see cref="CircuitBreakerOptions.MaxBackoff"/>).
/// Kept separately from <see cref="CurrentBackoff"/> so each successive doubling step doubles the
/// pure base rather than an already-jittered value — doubling a jittered value would compound
/// jitter multiplicatively across steps instead of applying it once per step (M3-12 jitter-compounding fix).
/// </param>
/// <param name="CurrentBackoff">
/// <see cref="BaseBackoff"/> with jitter applied — this is the open-duration gate:
/// <see cref="NextProbeAt"/> is set to the time the breaker opened plus this value (M3-12).
/// </param>
/// <param name="LastFailureAt">Timestamp of the most recent failure, if any.</param>
/// <param name="LastSuccessAt">Timestamp of the most recent success, if any.</param>
/// <param name="LastError">Most recent error message, if any.</param>
/// <param name="LastOutcome">
/// arb-mhd2: WHY the most recent failure happened, as a closed
/// <see cref="SourceStatusOutcome"/>. The companion to
/// <paramref name="LastError"/>, written in the same place from the same exception, but unlike it
/// this value is safe on the unauthenticated <c>GET /api/status</c> — an enum cannot be minted from
/// upstream text. That route publishes this; <paramref name="LastError"/> moved behind the admin
/// key. <see cref="SourceStatusOutcome.None"/> while nothing has failed.
/// </param>
/// <param name="LastUpstreamStatusCode">
/// arb-mhd2: the HTTP status upstream answered the most recent failure with, or null when the
/// failure carried none (a connection failure, a timeout, an internal fault). Captured from the
/// exception at the writer for the same reason <paramref name="LastOutcome"/> is: re-deriving it by
/// parsing <paramref name="LastError"/>'s prose would make it depend on the scrubber's wording.
/// Admin-gated — it is served by the diagnostics route, never by <c>/api/status</c>.
/// </param>
/// <param name="NextProbeAt">When the breaker may next attempt a probe call while Open. Null when Closed.</param>
public sealed record CircuitBreakerSnapshot(
    CircuitState State,
    int ConsecutiveFailures,
    TimeSpan BaseBackoff,
    TimeSpan CurrentBackoff,
    DateTimeOffset? LastFailureAt,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    SourceStatusOutcome LastOutcome,
    int? LastUpstreamStatusCode,
    DateTimeOffset? NextProbeAt)
{
    /// <summary>The initial state for a source that has never recorded a call: Closed, zero failures, no backoff.</summary>
    public static CircuitBreakerSnapshot Initial { get; } = new(
        State: CircuitState.Closed,
        ConsecutiveFailures: 0,
        BaseBackoff: TimeSpan.Zero,
        CurrentBackoff: TimeSpan.Zero,
        LastFailureAt: null,
        LastSuccessAt: null,
        LastError: null,
        LastOutcome: SourceStatusOutcome.None,
        LastUpstreamStatusCode: null,
        NextProbeAt: null);
}
