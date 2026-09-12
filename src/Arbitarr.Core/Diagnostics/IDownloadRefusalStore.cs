namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// arb-v3w: durable storage for the per-source download refusals surfaced as <c>/api/status</c>
/// health items, so a restart does not hide a misconfiguration that is still in force.
///
/// <para>Defined in Core, implemented in Arbitarr.Data (<c>DownloadRefusalStore</c>), for the same
/// reason <see cref="Arbitarr.Core.Releases.IReleaseLookupStore"/> is: Core carries no reference to
/// the persistence layer, so the contract lives here and EF Core stays on the other side of it.</para>
///
/// <para>Rows are bounded by the number of HISTORICALLY configured sources — one row per source,
/// upserted, and deleted outright on a successful grab. A row also outlives the source itself, since
/// nothing deletes it when the operator removes that source, which is why
/// <see cref="PruneUnknownSourcesAsync"/> exists and is called once at rehydration. There is still no
/// time-based prune predicate and no <c>MaintenanceJob</c> pass: the set is bounded by the names that
/// have ever been configured, not by elapsed time.</para>
///
/// <para>NO UPSTREAM TEXT IS STORED. A <see cref="DownloadRefusal.Reason"/> is built by the caller
/// from the configured source name and an int status code only (see
/// <c>DownloadProxyEndpoint</c>'s catch block), and <c>/api/status</c> is un-gated, so this table
/// never becomes a way for an upstream response to reach a public surface.</para>
/// </summary>
public interface IDownloadRefusalStore
{
    /// <summary>
    /// Writes <paramref name="refusal"/> as this source's outstanding refusal, replacing any row
    /// already held for the same source name rather than adding a second one (the source name is
    /// uniquely indexed). The caller supplies the already-resolved
    /// <see cref="DownloadRefusal.ObservedSinceUtc"/>, so the "first observed" instant a repeat must
    /// preserve is decided in one place — the tracker — rather than twice.
    /// </summary>
    Task UpsertAsync(DownloadRefusal refusal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes <paramref name="sourceName"/>'s row, if any. Called ONLY from a genuinely successful
    /// download of a payload from that source — the same single clearing event the in-memory tracker
    /// honours.
    /// </summary>
    Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default);

    /// <summary>Every persisted refusal, ordered by source name. Empty when nothing is refused.</summary>
    Task<IReadOnlyList<DownloadRefusal>> LoadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// arb-pu58: deletes every row whose source name is NOT in <paramref name="knownSourceNames"/>,
    /// and returns how many were removed. Called once, from rehydration, before the surviving rows
    /// are replayed into the tracker.
    ///
    /// <para><b>Why this is needed at all.</b> A row is deleted only on a successful grab, so removing
    /// a source while its refusal stands leaves the row behind; rehydration would replay it and
    /// <c>StatusEndpoint</c> maps every snapshot entry to a health item without cross-checking the
    /// configured sources. The Dashboard would then show a blocking item — and, since arb-apj, a
    /// notification — for a source that no longer exists, with no way for the operator to clear it:
    /// the only clearing event is a successful grab from a source they have just deleted.</para>
    ///
    /// <para><b><paramref name="knownSourceNames"/> is EVERY source row's display name, not the one
    /// source resolved in force.</b> The distinction is load-bearing in two directions. A DISABLED
    /// source still has a row and may be re-enabled, and its refusal is very likely still true, so
    /// pruning it would discard a real condition; but source resolution deliberately skips disabled
    /// rows (<c>SourceSeeder.ResolveFromDatabaseAsync</c> filters on <c>Enabled</c>), so a predicate
    /// built from the resolved configuration would delete exactly those. In the other direction, an
    /// install with no enabled source resolves to NOTHING, and that predicate would then blanket-
    /// delete every row on an ordinary unconfigured start. Row existence in <c>Sources</c> — which
    /// only <c>SourceRepository.DeleteAsync</c> ends — is the condition that actually means "this
    /// source is gone".</para>
    ///
    /// <para>An EMPTY <paramref name="knownSourceNames"/> therefore means the sources table is
    /// genuinely empty, and every refusal row is genuinely orphaned. It is not a "skip the prune"
    /// signal, and must not be turned into one.</para>
    /// </summary>
    Task<int> PruneUnknownSourcesAsync(
        IReadOnlyCollection<string> knownSourceNames,
        CancellationToken cancellationToken = default);
}
