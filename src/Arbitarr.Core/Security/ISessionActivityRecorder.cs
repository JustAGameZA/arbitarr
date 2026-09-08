namespace Arbitarr.Core.Security;

/// <summary>
/// #44: records that a session just authenticated a request, which is what keeps an actively used
/// session from reaching its idle expiry.
///
/// <para><b>THIS MUST NOT BE A SYNCHRONOUS WRITE</b>, for exactly the reasons
/// <see cref="IApiKeyLastUsedRecorder"/> sets out at length — it is on the hot path of every
/// cookie-authenticated request, against a single-writer SQLite database that already serialises
/// the request's real work. The implementation coalesces (at most one write per session per
/// <see cref="ThrottleWindow"/>) and never writes inline. The return type is <c>void</c> rather
/// than a Task deliberately: there is nothing for a caller to await, and offering one would invite
/// precisely the inline write this exists to prevent.</para>
///
/// <para><b>WHY THE THROTTLE IS SAFE HERE, WHICH IS NOT OBVIOUS.</b> Throttling a last-used
/// timestamp only risks a stale display. Throttling THIS risks expiring a live session, because the
/// value it writes is what idle expiry is measured against — so the window must stay far smaller
/// than the shortest sane idle timeout. At one minute against a default of seven days the margin is
/// four orders of magnitude. If a future change makes the idle timeout configurable down to minutes,
/// this window is what has to move with it.</para>
/// </summary>
public interface ISessionActivityRecorder
{
    /// <summary>
    /// How stale a session's recorded activity may be. One minute, matching
    /// <see cref="IApiKeyLastUsedRecorder.ThrottleWindow"/> — the two solve the same problem on the
    /// same request path, and one number is easier to reason about than two that differ for no
    /// stated reason.
    /// </summary>
    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Notes that session <paramref name="sessionId"/> was used. Returns immediately; the write, if
    /// this call earns one, happens off the request path.
    /// </summary>
    void RecordSeen(long sessionId);
}
