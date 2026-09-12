using Microsoft.Extensions.Logging;

namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// arb-v3w: the durable tier behind <see cref="DownloadRefusalTracker"/>. Wraps an in-memory tracker
/// and mirrors every write to an <see cref="IDownloadRefusalStore"/>, so a fresh process can
/// rehydrate its health items rather than showing none until the next download happens to fail.
///
/// <para><b>Why a decorator and not a second write path.</b> The in-memory tracker stays the single
/// place that decides what an entry IS — in particular that a repeat preserves
/// <see cref="DownloadRefusal.ObservedSinceUtc"/>. This type only mirrors the decision that tracker
/// has already made, by reading the resulting entry back out of its snapshot and handing that to the
/// store. Two independent implementations of the preserve-the-first-instant rule would be two places
/// to get it wrong, and the observable symptom — a banner resetting its age on every Sonarr retry —
/// is exactly what arb-ln0 exists to prevent.</para>
///
/// <para><b>Reads never touch the store.</b> <see cref="Snapshot"/> returns the in-memory snapshot
/// and nothing else. <c>/api/status</c> is on the request path, and once <see cref="RehydrateAsync"/>
/// has run at startup the memory tier is authoritative — it is also the only writer. A per-request
/// query would add a database round trip to a public endpoint for an answer it already holds.</para>
///
/// <para><b>A failed store write must never fail the download proxy.</b> The write-through is
/// awaited, then any failure is logged at Warning and swallowed — the same posture
/// <c>SearchEndpoint</c> takes on the release-lookup store write (#190/arb-zwk) and
/// <c>ScopedEventSink</c> takes on every event. The failure is a durability problem, not a
/// correctness one for this request: the item is still shown for this process's lifetime, which is
/// precisely the behaviour that shipped before this class existed. Letting it escape would turn a
/// write fault into a 500 on a path whose whole job is to report that something else is broken.</para>
/// </summary>
public sealed class PersistentDownloadRefusalTracker : IDownloadRefusalTracker
{
    private readonly DownloadRefusalTracker _inner;
    private readonly Func<Func<IDownloadRefusalStore, CancellationToken, Task>, CancellationToken, Task> _withStoreAsync;
    private readonly ILogger? _logger;

    /// <param name="inner">The in-memory tracker that owns the entry semantics.</param>
    /// <param name="withStoreAsync">
    /// Runs one operation against a store. A delegate rather than a store instance because the store
    /// is scoped (it holds the scoped <c>ArbitarrDbContext</c>, which is not thread-safe) while this
    /// tracker is a singleton outliving every request scope. The composition root creates and
    /// disposes a scope per operation, exactly as it does for <c>PersistentReleaseLookup</c> and
    /// <c>ScopedEventSink</c>.
    /// </param>
    public PersistentDownloadRefusalTracker(
        DownloadRefusalTracker inner,
        Func<Func<IDownloadRefusalStore, CancellationToken, Task>, CancellationToken, Task> withStoreAsync,
        ILogger? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _withStoreAsync = withStoreAsync ?? throw new ArgumentNullException(nameof(withStoreAsync));
        _logger = logger;
    }

    /// <summary>
    /// Loads every persisted refusal into the in-memory tracker. Called once at startup, before the
    /// first request can read <see cref="Snapshot"/>.
    ///
    /// <para>Each row is replayed through the inner tracker with its PERSISTED instants, so a
    /// rehydrated entry reports when the condition actually began rather than when this process
    /// started — the whole point of persisting it. Nothing is written back: the replay reproduces the
    /// row that is already there.</para>
    ///
    /// <para>Unlike the write paths this does NOT swallow: a failure here is surfaced to the caller,
    /// which is the startup service, and that service decides (it logs and carries on — see its own
    /// doc). Swallowing here would leave the caller unable to tell an empty database from an
    /// unreadable one.</para>
    ///
    /// <para>arb-pu58: rows for sources that no longer exist are PRUNED first, in the same pass, so a
    /// source the operator removed cannot ghost the Dashboard. The prune and the load share one pass
    /// because the alternative — filtering the loaded list here and leaving the rows in place — would
    /// hide the item this run and resurrect it on every subsequent start.</para>
    /// </summary>
    /// <param name="knownSourceNames">
    /// Every configured source's name, supplied by the caller because Core knows nothing of the
    /// <c>Sources</c> table. See <see cref="IDownloadRefusalStore.PruneUnknownSourcesAsync"/> for why
    /// this is every source row rather than the single source resolved in force, and why an empty
    /// collection legitimately prunes everything.
    /// </param>
    /// <returns>How many orphaned rows were pruned.</returns>
    public async Task<int> RehydrateAsync(
        IReadOnlyCollection<string> knownSourceNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knownSourceNames);

        IReadOnlyList<DownloadRefusal> persisted = Array.Empty<DownloadRefusal>();
        var pruned = 0;

        await _withStoreAsync(
            async (store, token) =>
            {
                // Prune BEFORE loading, so the load cannot return a row this pass has just decided is
                // orphaned — one scope, one connection, and no window in which the two disagree.
                pruned = await store.PruneUnknownSourcesAsync(knownSourceNames, token).ConfigureAwait(false);
                persisted = await store.LoadAllAsync(token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var refusal in persisted)
        {
            // Two calls, deliberately, rather than an entry point that sets both instants directly:
            // the first fixes ObservedSinceUtc and the second advances LastObservedUtc while
            // preserving it, so the pair is restored BY the same rule that produced it. A
            // rehydrate-specific setter would be a second implementation of that rule, free to drift.
            _inner.RecordRefusal(refusal.SourceName, refusal.Reason, refusal.ObservedSinceUtc);

            if (refusal.LastObservedUtc != refusal.ObservedSinceUtc)
            {
                _inner.RecordRefusal(refusal.SourceName, refusal.Reason, refusal.LastObservedUtc);
            }
        }

        return pruned;
    }

    public async ValueTask RecordRefusalAsync(string sourceName, string reason, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        _inner.RecordRefusal(sourceName, reason, at);

        // Read the resulting entry back rather than reconstructing it here: only the inner tracker
        // knows whether this call fixed ObservedSinceUtc or preserved an earlier one.
        var recorded = _inner.Snapshot().FirstOrDefault(r => string.Equals(r.SourceName, sourceName, StringComparison.Ordinal));
        if (recorded is null)
        {
            // Unreachable in practice, not merely unlikely: the line above just called
            // _inner.RecordRefusal(sourceName, ...) under the inner tracker's own lock, and that
            // call is the only writer of _inner's dictionary (this type never mutates it directly).
            // A write immediately followed by a read of the same key, with no other writer able to
            // interleave, cannot come back empty. Kept as a guard rather than an assertion because a
            // future change to the inner tracker's keying (e.g. a comparer change) is exactly the
            // kind of edit that could make this reachable, and a null-check degrading to "skip the
            // persist" is a far smaller failure than a NullReferenceException on the download path.
            return;
        }

        await WriteThroughAsync(
            (store, token) => store.UpsertAsync(recorded, token),
            "Persisting a download-refusal health item failed; it is still shown for this process's lifetime but would not survive a restart.")
            .ConfigureAwait(false);
    }

    public async ValueTask RecordSuccessfulGrabAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        _inner.RecordSuccessfulGrab(sourceName);

        await WriteThroughAsync(
            (store, token) => store.DeleteAsync(sourceName, token),
            "Clearing a persisted download-refusal health item failed; it is cleared in memory but a restart would show it again.")
            .ConfigureAwait(false);
    }

    public IReadOnlyList<DownloadRefusal> Snapshot() => _inner.Snapshot();

    private async Task WriteThroughAsync(Func<IDownloadRefusalStore, CancellationToken, Task> operation, string failureMessage)
    {
        try
        {
            // CancellationToken.None: the write describes a download that has already been answered
            // one way or the other, and must outlive a client that disconnects mid-write — the same
            // reasoning DownloadProxyEndpoint states for the activity event it records alongside it.
            await _withStoreAsync(operation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The fixed message and the exception only — never the reason text and never a source
            // URL. This lands in the persistent log store served at /api/admin/logs (CLAUDE.md §1).
            _logger?.LogWarning(ex, "{FailureMessage}", failureMessage);
        }
    }
}
