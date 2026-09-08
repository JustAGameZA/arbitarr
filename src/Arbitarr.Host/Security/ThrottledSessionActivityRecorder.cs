using System.Collections.Concurrent;
using Arbitarr.Core.Security;
using Arbitarr.Data.Security;
using Microsoft.Extensions.DependencyInjection;

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
/// </summary>
public sealed class ThrottledSessionActivityRecorder : ISessionActivityRecorder
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastWrittenAt = new();
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

        _ = Task.Run(() => WriteAsync(sessionId, now));
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
