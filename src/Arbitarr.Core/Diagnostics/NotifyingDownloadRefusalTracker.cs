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
/// answers "what just changed", and neither has to know the other's job. Second, arb-v3w replaced
/// the tracker's backing store; confining the transition logic to a wrapper meant that change and
/// this one touched disjoint code. The Host now composes
/// <c>Notifying( Persistent( DownloadRefusalTracker ) )</c> — this decorator OUTERMOST, so the edge
/// it reports is read from state that has already been persisted.</para>
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

    /// <summary>
    /// Serialises each method's read-mutate-read triple so two concurrent calls for the same source
    /// (Sonarr retrying a refused `/download` in parallel) cannot both observe the pre-mutation state
    /// and either double-raise an edge or drop one. Any lock the inner tracker takes is always taken
    /// INSIDE this one (via <see cref="IsRefused"/> and the inner call), never the reverse, so there
    /// is no lock-order inversion.
    ///
    /// <para>A <see cref="SemaphoreSlim"/> rather than a <c>lock</c> because arb-v3w made the inner
    /// write asynchronous — the persisting tracker awaits a SQLite round-trip — and <c>await</c> is
    /// not permitted inside a <c>lock</c>. The semantics the gate has to provide are unchanged: one
    /// caller at a time through read-mutate-read. It must therefore be held ACROSS the inner await,
    /// which is exactly why the synchronous primitive cannot be used here.</para>
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

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

    public async ValueTask RecordRefusalAsync(string sourceName, string reason, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        bool appeared;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wasPresent = IsRefused(sourceName);
            await _inner.RecordRefusalAsync(sourceName, reason, at, cancellationToken).ConfigureAwait(false);
            appeared = !wasPresent && IsRefused(sourceName);
        }
        finally
        {
            _gate.Release();
        }

        // Raised AFTER the gate is released, deliberately: the callback is the app's, and holding the
        // serialising gate across it would let a slow notifier stall every other source's refusal.
        if (appeared)
        {
            Raise(sourceName, DownloadRefusalTransition.Appeared);
        }
    }

    public async ValueTask RecordSuccessfulGrabAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        bool cleared;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wasPresent = IsRefused(sourceName);
            await _inner.RecordSuccessfulGrabAsync(sourceName, cancellationToken).ConfigureAwait(false);
            cleared = wasPresent && !IsRefused(sourceName);
        }
        finally
        {
            _gate.Release();
        }

        if (cleared)
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
