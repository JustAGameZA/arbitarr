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
/// <param name="ConsecutiveFailedCycles">How many cycles have faulted in a row (0 once a cycle completes without faulting).</param>
public sealed record RefreshWorkerHealth(
    bool Enabled,
    DateTimeOffset? LastCycleStartedUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    int LastCycleCandidates,
    int LastCycleRefreshed,
    int LastCycleFailed,
    string? LastError,
    int ConsecutiveFailedCycles)
{
    /// <summary>The snapshot before any cycle has ever run.</summary>
    public static RefreshWorkerHealth NotStarted(bool enabled) =>
        new(enabled, null, null, 0, 0, 0, null, 0);
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

    /// <summary>Records that the current cycle faulted before completing (e.g. the store threw).</summary>
    void CycleFaulted(DateTimeOffset completedUtc, string errorMessage);
}

/// <summary>
/// Default <see cref="IRefreshWorkerHealth"/>: a thread-safe singleton snapshot holder. Registered
/// once per worker instance in the Host and shared with <see cref="Dashboard"/>'s status endpoint via
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
                ConsecutiveFailedCycles = 0,
            };
        }
    }

    public void CycleFaulted(DateTimeOffset completedUtc, string errorMessage)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastCycleCompletedUtc = completedUtc,
                LastError = errorMessage,
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

    public void CycleFaulted(DateTimeOffset completedUtc, string errorMessage)
    {
    }
}
