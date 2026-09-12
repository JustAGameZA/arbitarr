namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// arb-v3w: durable storage for the per-source download refusals surfaced as <c>/api/status</c>
/// health items, so a restart does not hide a misconfiguration that is still in force.
///
/// <para>Defined in Core, implemented in Arbitarr.Data (<c>DownloadRefusalStore</c>), for the same
/// reason <see cref="Arbitarr.Core.Releases.IReleaseLookupStore"/> is: Core carries no reference to
/// the persistence layer, so the contract lives here and EF Core stays on the other side of it.</para>
///
/// <para>Rows are bounded by the number of CONFIGURED sources — one row per source, upserted, and
/// deleted outright on a successful grab — so there is deliberately no prune predicate and no
/// <c>MaintenanceJob</c> pass for this table. Nothing here can accumulate.</para>
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
}
