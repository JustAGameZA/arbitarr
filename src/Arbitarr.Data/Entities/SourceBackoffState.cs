namespace Arbitarr.Data.Entities;

/// <summary>
/// arb-x7w8.10: the durable half of per-source backoff — ONE ROW PER SOURCE, holding how long a
/// failing source is held off, how far its escalation has climbed, and whether it has been disabled
/// outright. Persisted rather than held in memory because the conditions it records outlive the
/// process: a rejected API key is still rejected after a restart, and a 24-hour budget window does
/// not begin again because the host did.
///
/// <para><b>This is NOT a second circuit breaker, and must not be folded into
/// <c>IAsyncCircuitBreaker</c>.</b> The breaker is an in-process, short-window fault DETECTOR that
/// recovers by itself within minutes and that no operator acts on. This row is the opposite on every
/// axis: it is durable, it is operator-facing, and a permanent disable never recovers without a
/// human. The relationship is the one <see cref="DownloadRefusalEntry"/> already has to the breaker
/// — a durable, operator-visible condition ALONGSIDE a transient in-process one, not a special case
/// of it. See <see href="../../../docs/adr/0020-api-hit-budget-and-durable-backoff.md">ADR 0020</see>,
/// whose Context section exists to answer precisely this temptation; merging them reverses that
/// decision and needs an ADR superseding it.</para>
///
/// <para><b>This table cannot grow without bound, and needs no prune.</b>
/// <see cref="SourceName"/> is uniquely indexed and exactly one row exists per source, so the row
/// count is bounded by the number of sources ever configured — the same bound
/// <see cref="DownloadRefusalEntry"/> relies on under ADR 0016. There is deliberately NO
/// <c>MaintenanceJob</c> <c>PrunePredicates</c> entry and NO <c>MaintenanceJobResult</c> field for
/// it: the bound is set membership, not a function of elapsed time, and adding a time-based sweep
/// would delete a permanent disable that is still true. (The API-HIT EVENTS the budget counts are a
/// different matter and ARE pruned — they ride the existing 7-day
/// <c>EventRetentionPolicy.OperationalRetention</c> sweep like every other operational kind, so they
/// need no new prune either.)</para>
///
/// <para><b>Three states, and they must stay distinguishable.</b> Budgeted (skipped because the
/// allowance is spent — not represented here at all, since it is derived from events), backing off
/// (<see cref="DisabledUntil"/> in the future), and permanently disabled
/// (<see cref="IsPermanentlyDisabled"/>) are three different things to tell an operator. Collapsing
/// them into one "unavailable" reports a broken key as a temporary pause and removes the signal to
/// go and fix it.</para>
/// </summary>
public sealed class SourceBackoffState
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The CONFIGURED source name this state belongs to. Uniquely indexed: a source has exactly one
    /// backoff state, and two rows answering one source would make "is this source held off?"
    /// unanswerable — the same reason <see cref="DownloadRefusalEntry.SourceName"/> is unique.
    /// </summary>
    public required string SourceName { get; set; }

    /// <summary>
    /// The instant until which this source is held off, or null when it is not backing off. A value
    /// in the PAST means the backoff has elapsed and the source may be called again; it is not
    /// cleared eagerly, because the level it was reached at is still live information until the next
    /// outcome resolves it.
    /// </summary>
    public DateTimeOffset? DisabledUntil { get; set; }

    /// <summary>
    /// How far escalation has climbed, as an index into
    /// <see cref="Sources.SourceBackoffPolicy.Periods"/>. Zero means "not escalated".
    ///
    /// <para><b>Success resets this to zero outright, and does NOT decrement it.</b> A recovered
    /// indexer is recovered, not half-punished for a fault that is over. Decrementing (Prowlarr's
    /// model) leaves a source that failed six times and then succeeded still carrying five levels of
    /// suspicion it has just disproved.</para>
    /// </summary>
    public int DisabledLevel { get; set; }

    /// <summary>
    /// What the last observed call outcome was, as a <see cref="Sources.SourceCallOutcome"/> name —
    /// operator-facing context for why the row is in the state it is in. Null before any outcome has
    /// been recorded.
    /// </summary>
    public string? LastOutcome { get; set; }

    /// <summary>
    /// Set when this source failed AUTHENTICATION, which is not a transient fault: no amount of
    /// waiting fixes a wrong key or a revoked account.
    ///
    /// <para><b>It bypasses escalation entirely rather than climbing to the longest period.</b>
    /// Escalating an auth failure converts a fault an operator could fix in a minute into a slow
    /// permanent trickle that LOOKS intermittent, and each retry both spends budget and re-presents
    /// a bad credential to the indexer. Clearing this flag is an operator action, never an automatic
    /// one — see ADR 0020's "Treat authentication failures as transient and escalate them", rejected.
    /// </para>
    /// </summary>
    public bool IsPermanentlyDisabled { get; set; }

    /// <summary>When this state was last written.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
