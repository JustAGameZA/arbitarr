namespace Arbitarr.Data.Entities;

/// <summary>
/// Discriminates what an <see cref="EventEntry"/> row records. One store serves two consumers
/// (#55's activity/history surface and #54's AI verdict review queue) rather than two overlapping
/// tables that would inevitably drift — see the plan's §2 for both issues.
///
/// <see cref="Decision"/> rows are #54's territory: a pipeline decision (suppress / de-rank / pass),
/// which #54 hangs a nullable review verdict off of. Every other member is a non-decision,
/// operational event #55 needs (worker cycle ran, snapshot refreshed, a search was served from
/// cache vs. live, a source failed) — see plan §3.1 for why these specific kinds and no others
/// (an event log that records everything is just <c>docker logs</c> with extra steps).
///
/// This enum is intentionally the ONLY discriminator between the two consumers' concerns; #54 and
/// #55 both read the same <see cref="ArbitarrDbContext.Events"/> set and filter by this value rather
/// than by table.
/// </summary>
public enum EventKind
{
    /// <summary>
    /// A pipeline decision (release, decision, reason, shadow-mode state at decision time). #54's
    /// review verdict is a nullable column on this same row, never a second table.
    /// </summary>
    Decision = 0,

    /// <summary>The worker cycle ran, with what it did (not merely that it ticked — plan §3.1).</summary>
    WorkerCycle = 1,

    /// <summary>A query snapshot was refreshed, and why (due / forced / lead-time).</summary>
    SnapshotRefreshed = 2,

    /// <summary>A search was served, distinguishing cache-served from live-query-served (plan §3.1/AC3).</summary>
    SearchServed = 3,

    /// <summary>A source failed, with the failure kind — also #57's notification trigger (plan §3.3).</summary>
    SourceFailed = 4,

    /// <summary>
    /// One outbound SEARCH call was made to one source (arb-x7w8.10). This is the per-source API-hit
    /// record the query budget is counted from, and it exists because nothing written before it
    /// recorded one — see <see href="../../../docs/adr/0020-api-hit-budget-and-durable-backoff.md">ADR
    /// 0020</see>.
    ///
    /// <para><b>NOT <see cref="SearchServed"/>, and the distinction is the whole reason this kind
    /// exists.</b> SearchServed is written once per CLIENT REQUEST and is written even when nothing
    /// went upstream — the search endpoint computes a <c>servedWithoutUpstreamCall</c> flag precisely
    /// because a snapshot or a warm cache answers without calling a source. Counting it would count
    /// client traffic and charge the operator's allowance for answers that never left the process.
    /// This kind is written AT THE OUTBOUND CALL SITE, on the call itself, and never on a cache hit
    /// or a snapshot serve.</para>
    /// </summary>
    SourceQueryHit = 5,

    /// <summary>
    /// One outbound GRAB (download fetch) was made to one source (arb-x7w8.10) — the per-source
    /// API-hit record the grab budget is counted from.
    ///
    /// <para><b>A successful grab was evented nowhere before this kind.</b> The download path writes
    /// a <see cref="SourceFailed"/> only on failure, so the one call that actually spends the
    /// operator's grab allowance left no record at all. Written at the outbound call site for the
    /// same reason <see cref="SourceQueryHit"/> is.</para>
    /// </summary>
    SourceGrabHit = 6,
}
