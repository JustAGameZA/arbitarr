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
}
