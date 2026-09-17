using Arbitarr.Core.Diagnostics;

namespace Arbitarr.Core.Caching;

/// <summary>
/// A point-in-time snapshot of the proactive-refresh worker's health (M7-7, R20), as exposed via
/// <c>/api/status</c>'s worker block. Deliberately message-only on failure (<see cref="LastError"/>)
/// — never a stack trace, path, or URL — since this snapshot is surfaced to an operator dashboard.
/// </summary>
/// <param name="Enabled">Whether proactive refresh is turned on (<see cref="Settings.SettingKey.WorkerEnabled"/>).</param>
/// <param name="LastCycleStartedUtc">When the most recent cycle began, or null if no cycle has run yet.</param>
/// <param name="LastCycleCompletedUtc">When the most recent cycle finished (success or failure), or null if no cycle has completed yet.</param>
/// <param name="LastCycleCandidates">How many refresh candidates the most recent cycle selected.</param>
/// <param name="LastCycleRefreshed">How many of those candidates were successfully refreshed in the most recent cycle.</param>
/// <param name="LastCycleFailed">How many of those candidates failed to refresh in the most recent cycle.</param>
/// <param name="LastError">The most recent cycle-level failure's message, or null if the last cycle did not fault.</param>
/// <param name="LastOutcome">
/// arb-mhd2: WHY the most recent cycle faulted, as a closed <see cref="SourceStatusOutcome"/>. The
/// companion to <paramref name="LastError"/>, set in the same call from the same exception, but
/// safe on the unauthenticated <c>/api/status</c> where the message is not — an enum cannot be
/// minted from upstream text. That route publishes this; the message moved behind the admin key.
/// <see cref="SourceStatusOutcome.None"/> whenever <paramref name="LastError"/> is null, including
/// after a clean cycle clears a previous fault.
///
/// <para>Unlike the circuit breaker's equivalent this is NOT persisted: worker health has always
/// been in-memory and starts over at <c>NotStarted</c> on every boot, so there is no stored row for
/// an outcome to go stale in and no <see cref="SourceStatusOutcome.Unknown"/> case here.</para>
/// </param>
/// <param name="ConsecutiveFailedCycles">How many cycles have faulted in a row (0 once a cycle completes without faulting).</param>
public sealed record RefreshWorkerHealth(
    bool Enabled,
    DateTimeOffset? LastCycleStartedUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    int LastCycleCandidates,
    int LastCycleRefreshed,
    int LastCycleFailed,
    string? LastError,
    SourceStatusOutcome LastOutcome,
    int ConsecutiveFailedCycles)
{
    /// <summary>The snapshot before any cycle has ever run.</summary>
    public static RefreshWorkerHealth NotStarted(bool enabled) =>
        new(enabled, null, null, 0, 0, 0, null, SourceStatusOutcome.None, 0);
}

/// <summary>
/// Thread-safe sink <see cref="RefreshWorker"/> reports cycle progress into, and the read side the
/// dashboard/status endpoint consumes. A no-op default is used wherever a worker is constructed
/// without an explicit tracker (existing tests, other call sites) so the health feature is additive.
/// </summary>
public interface IRefreshWorkerHealth
{
    /// <summary>Current health snapshot.</summary>
    RefreshWorkerHealth Snapshot { get; }

    /// <summary>Records that a new cycle has started, selecting <paramref name="candidateCount"/> entries.</summary>
    void CycleStarted(DateTimeOffset startedUtc, bool enabled, int candidateCount);

    /// <summary>Records that the current cycle completed, with per-entry outcome counts.</summary>
    void CycleCompleted(DateTimeOffset completedUtc, int refreshed, int failed);

    /// <summary>
    /// Records that the current cycle faulted before completing (e.g. the store threw).
    ///
    /// <para>arb-mhd2: <paramref name="outcome"/> is a required parameter rather than something
    /// derived here, so the closed value and the message are supplied together from the one
    /// exception at the call site that caught it. Deriving it inside from
    /// <paramref name="errorMessage"/> would be the projection-time classification of sanitised text
    /// this design exists to forbid.</para>
    /// </summary>
    void CycleFaulted(DateTimeOffset completedUtc, string errorMessage, SourceStatusOutcome outcome);
}

/// <summary>
/// Default <see cref="IRefreshWorkerHealth"/>: a thread-safe singleton snapshot holder. Registered
/// once per worker instance in the Host and shared with <c>Arbitarr.Api.Dashboard</c>'s status endpoint via
/// DI; tests construct their own instance directly since it needs no dependencies.
/// </summary>
public sealed class RefreshWorkerHealthTracker : IRefreshWorkerHealth
{
    private readonly object _gate = new();
    private RefreshWorkerHealth _snapshot;

    public RefreshWorkerHealthTracker(bool enabled = true)
    {
        _snapshot = RefreshWorkerHealth.NotStarted(enabled);
    }

    public RefreshWorkerHealth Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void CycleStarted(DateTimeOffset startedUtc, bool enabled, int candidateCount)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Enabled = enabled,
                LastCycleStartedUtc = startedUtc,
                LastCycleCandidates = candidateCount,
            };
        }
    }

    public void CycleCompleted(DateTimeOffset completedUtc, int refreshed, int failed)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastCycleCompletedUtc = completedUtc,
                LastCycleRefreshed = refreshed,
                LastCycleFailed = failed,
                LastError = null,
                // arb-mhd2: cleared with the message, not left behind. A stale outcome beside a
                // null message would publish a failure reason for a cycle that succeeded.
                LastOutcome = SourceStatusOutcome.None,
                ConsecutiveFailedCycles = 0,
            };
        }
    }

    public void CycleFaulted(DateTimeOffset completedUtc, string errorMessage, SourceStatusOutcome outcome)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastCycleCompletedUtc = completedUtc,
                LastError = errorMessage,
                LastOutcome = outcome,
                ConsecutiveFailedCycles = _snapshot.ConsecutiveFailedCycles + 1,
            };
        }
    }
}

/// <summary>No-op tracker: the default whenever a <see cref="RefreshWorker"/> is constructed without an explicit health sink.</summary>
public sealed class NullRefreshWorkerHealth : IRefreshWorkerHealth
{
    public static readonly NullRefreshWorkerHealth Instance = new();

    public RefreshWorkerHealth Snapshot => RefreshWorkerHealth.NotStarted(enabled: false);

    public void CycleStarted(DateTimeOffset startedUtc, bool enabled, int candidateCount)
    {
    }

    public void CycleCompleted(DateTimeOffset completedUtc, int refreshed, int failed)
    {
    }

    public void CycleFaulted(DateTimeOffset completedUtc, string errorMessage, SourceStatusOutcome outcome)
    {
    }
}
