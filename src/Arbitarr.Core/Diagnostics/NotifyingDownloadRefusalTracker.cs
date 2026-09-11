namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// Which edge of a source's refusal state was just crossed (arb-apj).
/// </summary>
public enum DownloadRefusalTransition
{
    /// <summary>The source had no outstanding refusal and now has one (none -> present).</summary>
    Appeared,

    /// <summary>The source had an outstanding refusal and now has none (present -> none).</summary>
    Cleared,
}

/// <summary>
/// Decorates any <see cref="IDownloadRefusalTracker"/> and reports the EDGES of each source's
/// refusal state — one callback when a source's health item first appears and one when it clears —
/// so an operator hears about the condition starting and ending, and hears nothing at all while it
/// merely persists (arb-apj).
///
/// <para><b>Why a decorator rather than a callback on <see cref="DownloadRefusalTracker"/>.</b> Two
/// reasons, and the second is the load-bearing one. First, it keeps the tracker a plain state
/// holder with no notification concept in it at all: the tracker answers "what is refused", this
/// answers "what just changed", and neither has to know the other's job. Second, arb-v3w replaces
/// the tracker's backing store; confining the transition logic to a wrapper means that change and
/// this one touch disjoint code. Register the decorator around whatever
/// <see cref="IDownloadRefusalTracker"/> the Host composes and this keeps working unchanged.</para>
///
/// <para><b>The edges are computed from the INNER tracker's own state, never from a duplicate set
/// held here.</b> Before each mutation this asks the inner tracker whether the source is currently
/// refused and asks again afterwards; a transition is a difference between those two answers. A
/// private copy of "which sources are refused" would be a second source of truth that drifts the
/// moment the inner tracker gains any other way to change — a persisted tracker rehydrating at
/// startup, say — and would then either notify about a condition that is not there or stay silent
/// about one that is. Reading through costs one extra snapshot per refusal, on a path that has just
/// finished an upstream HTTP request.</para>
///
/// <para><b>The callback must not throw and must not block.</b> It runs inline on the download
/// proxy's request path, which is the path a refusal has already failed; an exception thrown here
/// would turn a clean 502 into a 500, and a slow callback would hold the request open. The Host's
/// registration satisfies both by handing off to a fire-and-forget delivery that swallows its own
/// failures, exactly as §3.4/AC6 requires of every notification path. This type enforces the first
/// half itself: a throwing callback is caught and dropped, because a broken notifier must never be
/// able to break a download.</para>
/// </summary>
public sealed class NotifyingDownloadRefusalTracker : IDownloadRefusalTracker
{
    private readonly IDownloadRefusalTracker _inner;
    private readonly Action<string, DownloadRefusalTransition> _onTransition;

    /// <param name="inner">The tracker that actually holds the state.</param>
    /// <param name="onTransition">
    /// Invoked with the CONFIGURED source name and which edge was crossed, once per edge. Never
    /// invoked for a repeat while the condition persists — that is the whole point.
    /// </param>
    public NotifyingDownloadRefusalTracker(
        IDownloadRefusalTracker inner,
        Action<string, DownloadRefusalTransition> onTransition)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onTransition = onTransition ?? throw new ArgumentNullException(nameof(onTransition));
    }

    public void RecordRefusal(string sourceName, string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        var wasPresent = IsRefused(sourceName);
        _inner.RecordRefusal(sourceName, reason, at);

        if (!wasPresent && IsRefused(sourceName))
        {
            Raise(sourceName, DownloadRefusalTransition.Appeared);
        }
    }

    public void RecordSuccessfulGrab(string sourceName)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        var wasPresent = IsRefused(sourceName);
        _inner.RecordSuccessfulGrab(sourceName);

        if (wasPresent && !IsRefused(sourceName))
        {
            Raise(sourceName, DownloadRefusalTransition.Cleared);
        }
    }

    public IReadOnlyList<DownloadRefusal> Snapshot() => _inner.Snapshot();

    /// <summary>
    /// Whether the inner tracker currently holds a refusal for this source. Ordinal comparison, to
    /// match <see cref="DownloadRefusalTracker"/>'s own keying: reading the same source name under
    /// two different comparers is how a transition gets missed in one direction and duplicated in
    /// the other.
    /// </summary>
    private bool IsRefused(string sourceName)
    {
        foreach (var refusal in _inner.Snapshot())
        {
            if (string.Equals(refusal.SourceName, sourceName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Invokes the callback, swallowing anything it throws. See the type's note: this runs inline on
    /// the download path, so a failure in the notifier must cost a notification and nothing else.
    /// </summary>
    private void Raise(string sourceName, DownloadRefusalTransition transition)
    {
        try
        {
            _onTransition(sourceName, transition);
        }
        catch
        {
            // Deliberately swallowed, and deliberately not logged here: this type has no logger by
            // design (it lives in Core beside a dependency-free tracker), and the Host's callback
            // owns its own error handling. Rethrowing would fail a download because a webhook is
            // misconfigured, which is exactly the coupling AC6 forbids.
        }
    }
}
