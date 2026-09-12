namespace Arbitarr.Data.Entities;

/// <summary>
/// arb-v3w: the durable half of the sticky download-refusal health item — ONE ROW PER SOURCE,
/// upserted on each refusal and deleted outright on a successful grab, so the condition survives a
/// restart. Before this table existed the tracker was process-lifetime memory only, and because the
/// NZBHydra2 "NZB access type: Redirect" misconfiguration that causes it does NOT end at a restart,
/// a fresh process showed a clean dashboard while every download still failed — the exact
/// invisibility ADR 0014 records as the original defect.
///
/// <para><b>This table cannot grow without bound.</b> <see cref="SourceName"/> is uniquely indexed
/// and a row is deleted the moment the source serves a payload, so the row count is bounded above by
/// the number of HISTORICALLY configured sources — every name that has ever been configured, pruned
/// at rehydration — rather than by the number configured right now. The two differ because nothing
/// deletes a row when the operator removes the source it belongs to: a successful grab is the only
/// clearing event, and a deleted source can never serve one. That gap is arb-pu58, and the prune
/// that closes it is <c>IDownloadRefusalStore.PruneUnknownSourcesAsync</c>, called once per start
/// from <c>DownloadRefusalRehydrationService</c>. There is still deliberately no <c>ExpiresAt</c> and
/// no <c>MaintenanceJob</c> pass: the bound is a set of names, not a function of elapsed time.</para>
///
/// <para><b>NO UPSTREAM TEXT IS STORED.</b> <see cref="Reason"/> is built by
/// <c>DownloadProxyEndpoint</c> from the configured source name and an int status code only — never
/// from upstream-supplied text such as a <c>Location</c> header. That rule is load-bearing because
/// <c>/api/status</c>, where this value is surfaced, is <c>RouteClassification.PublicRead</c> and
/// un-gated; see the comments in that endpoint's catch block, which must be preserved.</para>
/// </summary>
public sealed class DownloadRefusalEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The CONFIGURED source name this refusal was recorded against. Uniquely indexed: the tracker
    /// holds exactly one outstanding refusal per source, and two rows answering one source would
    /// make "how long has this been broken?" unanswerable.
    /// </summary>
    public required string SourceName { get; set; }

    /// <summary>Why the download was refused. See the type-level remarks on what may appear here.</summary>
    public required string Reason { get; set; }

    /// <summary>
    /// When this source FIRST refused. Preserved across repeats by the tracker and written through
    /// unchanged — this is the field the whole bead exists to make survive a restart.
    /// </summary>
    public DateTimeOffset ObservedSinceUtc { get; set; }

    /// <summary>When this source most recently refused.</summary>
    public DateTimeOffset LastObservedUtc { get; set; }
}
