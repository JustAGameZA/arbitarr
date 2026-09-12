using System.Collections.Concurrent;
using Arbitarr.Core.Security;
using Arbitarr.Data.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Arbitarr.Host.Security;

/// <summary>
/// The real <see cref="ISessionActivityRecorder"/> (#44): stamps a session's last-seen time at most
/// once per <see cref="ISessionActivityRecorder.ThrottleWindow"/>, off the request path.
///
/// <para>Deliberately the same two-reduction mechanism as
/// <see cref="ThrottledApiKeyLastUsedRecorder"/> — coalesce in memory, then dispatch the write
/// detached — because it solves the same problem on the same hot path. See that type for the full
/// argument; the notes below cover only what differs for a session.</para>
///
/// <para><b>WHAT DIFFERS: THIS ONE IS LOAD-BEARING, NOT COSMETIC.</b> A missed last-used stamp on a
/// key costs an operator a slightly stale display. A missed stamp here shortens a live session
/// toward its idle expiry, so the failure mode is a user being signed out early rather than a wrong
/// number on a screen. That is why the swallowed-exception branch below logs at Warning and says so,
/// and why <see cref="ISessionActivityRecorder.ThrottleWindow"/> documents the margin it relies on.</para>
///
/// <para><b>WHY THE MAP CANNOT GROW WITHOUT BOUND</b> — the one place the key recorder's reasoning
/// does NOT carry over. API keys are minted by hand and number in the dozens; sessions accrue one
/// per login, forever, so "no eviction" would be a real leak on a long-lived process. Entries are
/// therefore dropped once they age past the throttle window, on the same call that walks them. The
/// sweep is bounded by how many DISTINCT sessions were seen in the last minute, which on a
/// single-operator appliance is a handful.</para>
///
/// <para><b>AND IT DRAINS AT SHUTDOWN (arb-acy9)</b>, as an <see cref="IHostedService"/>, for the
/// reason and by the mechanism set out on <see cref="ThrottledApiKeyLastUsedRecorder"/> — with more
/// at stake, since the stamp abandoned here is the one idle expiry is measured against, so losing it
/// at every restart shortens a live session rather than staling a display. The Warning below is
/// untouched: the drain removes the shutdown case that made it fire spuriously, which is what lets
/// the remaining occurrences be read as the genuine failures it was written for.</para>
///
/// <para>The drain's two escapes carry over unchanged, including the one that no timeout closes:
/// <see cref="DrainAsync"/> samples <c>_inFlight</c> once, so a stamp dispatched after that sample
/// is never waited for — and Kestrel may still be finishing requests, hence still calling
/// <see cref="RecordSeen"/>, while the drain runs. See the key recorder's copy for the full
/// argument.</para>
/// </summary>
public sealed class ThrottledSessionActivityRecorder : ISessionActivityRecorder, IHostedService
{
    /// <summary>
    /// How long <see cref="StopAsync"/> waits for in-flight writes. Deliberately the same figure as
    /// <see cref="ThrottledApiKeyLastUsedRecorder.DrainTimeout"/> — the two drain the same kind of
    /// write against the same database, and two numbers differing for no stated reason is worse than
    /// one. Referenced rather than redeclared so they cannot drift apart.
    /// </summary>
    public static TimeSpan DrainTimeout => ThrottledApiKeyLastUsedRecorder.DrainTimeout;

    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastWrittenAt = new();

    /// <summary>The writes dispatched but not yet finished; see the key recorder's field for why a
    /// set of tasks rather than a counter.</summary>
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ThrottledSessionActivityRecorder> _logger;

    public ThrottledSessionActivityRecorder(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<ThrottledSessionActivityRecorder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void RecordSeen(long sessionId)
    {
        var now = _timeProvider.GetUtcNow();

        // AddOrUpdate returns the value now in the map, and the update arm returns the EXISTING
        // value unchanged when inside the window. So "the map now holds `now`" is precisely "this
        // call won the slot", decided atomically — two concurrent requests on one session cannot
        // both win, which a read-then-write pair would allow.
        //
        // NOTE FOR A FUTURE CONCURRENCY TEST: this identity test picks out the winner only while
        // timestamps are DISTINCT. Under a FROZEN TimeProvider every racer reads the same instant
        // and all appear to win — an artefact of the fake clock, not a product bug. Assert the
        // throttle with an advancing FakeTimeProvider.
        var claimed = _lastWrittenAt.AddOrUpdate(
            sessionId,
            now,
            (_, previous) => now - previous >= ISessionActivityRecorder.ThrottleWindow ? now : previous);

        if (claimed != now)
        {
            return;
        }

        PruneStaleEntries(now);

        Dispatch(sessionId, now);
    }

    /// <inheritdoc />
    /// <remarks>Nothing to start: the drain is the only reason this is a hosted service.</remarks>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Drains in-flight writes while the DI provider is still alive. The shutdown token is not
    /// passed to the wait — it is already signalled on a forced shutdown, which would make the drain
    /// a no-op exactly when it is needed. See <see cref="ThrottledApiKeyLastUsedRecorder.StopAsync"/>.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken) => DrainAsync();

    /// <summary>
    /// Waits for every write dispatched so far, bounded by <see cref="DrainTimeout"/>. Tasks in the
    /// set never fault — <see cref="WriteAsync"/> swallows and logs — so there is nothing for
    /// <see cref="Task.WhenAll(IEnumerable{Task})"/> to rethrow.
    /// </summary>
    private async Task DrainAsync()
    {
        var pending = _inFlight.Keys.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var all = Task.WhenAll(pending);
        var completed = await Task.WhenAny(all, Task.Delay(DrainTimeout)).ConfigureAwait(false);

        if (!ReferenceEquals(completed, all))
        {
            // The captured total, not a re-count of the unfinished — see the key recorder's copy for
            // why a re-count can print a self-contradicting "Gave up waiting for 0".
            _logger.LogWarning(
                "Gave up waiting for {PendingCount} in-flight session activity write(s) after {DrainSeconds}s of shutdown; " +
                "the affected sessions may idle out earlier than their last real use.",
                pending.Length,
                DrainTimeout.TotalSeconds);
        }
    }

    /// <summary>
    /// Dispatches the write detached while keeping it visible to <see cref="DrainAsync"/>. The gate
    /// exists so the task is in the set before it can possibly complete; see
    /// <see cref="ThrottledApiKeyLastUsedRecorder"/>'s copy for the full argument.
    /// </summary>
    private void Dispatch(long sessionId, DateTimeOffset seenAt)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var write = Task.Run(async () =>
        {
            await gate.Task.ConfigureAwait(false);
            await WriteAsync(sessionId, seenAt).ConfigureAwait(false);
        });

        _inFlight[write] = 0;

        _ = write.ContinueWith(
            (completed, _) => _inFlight.TryRemove(completed, out byte _),
            state: null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        gate.SetResult();
    }

    /// <summary>
    /// Drops entries older than the throttle window. Safe to do while other threads are writing:
    /// removing an entry only costs the next call for that session an extra (correct) write, and
    /// the removal is conditional on the value still being the stale one, so a concurrent refresh
    /// is never discarded.
    /// </summary>
    private void PruneStaleEntries(DateTimeOffset now)
    {
        foreach (var (sessionId, writtenAt) in _lastWrittenAt)
        {
            if (now - writtenAt >= ISessionActivityRecorder.ThrottleWindow)
            {
                ((ICollection<KeyValuePair<long, DateTimeOffset>>)_lastWrittenAt)
                    .Remove(new KeyValuePair<long, DateTimeOffset>(sessionId, writtenAt));
            }
        }
    }

    private async Task WriteAsync(long sessionId, DateTimeOffset seenAt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<SessionRepository>();
            await repository.RecordSeenAsync(sessionId, seenAt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Swallowed for the same reason ThrottledApiKeyLastUsedRecorder swallows: bookkeeping
            // must never break the thing it is bookkeeping for, and the request this belongs to was
            // authorized and answered long before this ran. Warning rather than Debug because the
            // consequence here is not merely a stale field — a session whose activity never records
            // will eventually idle out under an operator who was actively using it, and this log
            // line is the only evidence of why.
            _logger.LogWarning(
                ex,
                "Failed to record activity for session {SessionId}; the request it belongs to was unaffected, " +
                "but the session may idle out early if this keeps failing.",
                sessionId);
        }
    }
}
