using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Sources;

/// <summary>
/// arb-x7w8.10: counts a source's outbound API hits inside its rolling limits window, and answers
/// whether a further call is within budget.
///
/// <para><b>THE COUNT SUMS <see cref="EventEntry.RepeatCount"/>, NOT ROWS, AND THAT IS THE WHOLE
/// CORRECTNESS PROPERTY OF THIS TYPE.</b> <c>EventRepository</c> FOLDS a repeated event onto the
/// previous row and increments its RepeatCount, and
/// <see cref="EventKind.SourceQueryHit"/>/<see cref="EventKind.SourceGrabHit"/> are opted into that
/// folding deliberately — a busy indexer produces exactly the repeated burst folding exists for. A
/// row count therefore UNDERCOUNTS, and it does so silently: the number is merely low, never
/// wrong-looking, so Arbitarr would sail past the operator's real limit while reporting a healthy
/// indexer. The enforcing test plants a row with RepeatCount above one and asserts the count matches
/// it; a test that only counts distinct events passes against the bug.</para>
///
/// <para><b>The tempting alternative is closed, not merely unattractive.</b> Making these events
/// stop folding — by rendering a per-occurrence value (a timestamp, a sequence number) into
/// <c>Detail</c> or <c>Reason</c> — would let a row count be correct, but those six fields are the
/// fold identity every other event consumer depends on, so it breaks folding for all of them. See
/// <see href="../../../docs/adr/0020-api-hit-budget-and-durable-backoff.md">ADR 0020</see>.</para>
///
/// <para><b>A null limit is UNLIMITED and is not zero.</b> Coalescing null to zero turns every
/// indexer whose limit an operator never configured off on the first search; coalescing zero to null
/// lets a limited one run past the cap its operator set. Both directions are silent. See
/// <see cref="Source.QueryLimit"/>.</para>
/// </summary>
public sealed class SourceApiHitCounter
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public SourceApiHitCounter(ArbitarrDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// The rolling window a <see cref="Source.LimitsUnit"/> denotes. Matched ordinally against
    /// <c>SourceRepository.KnownLimitsUnits</c>'s values; anything else falls back to the same 24
    /// hours the column defaults to, because a stored value outside that set cannot arrive through
    /// the repository boundary and an unrecognised one must not read as "no window at all".
    ///
    /// <para>Deliberately ROLLING — "how many hits fall in the last N hours" — rather than anchored
    /// to a clock hour or a daily reset. An anchored window has timezone and boundary semantics to
    /// get wrong (whose midnight; what happens across a DST shift) and gets them wrong invisibly, as
    /// a count that is merely off. A rolling window needs no agreement about when a day starts.</para>
    /// </summary>
    public static TimeSpan WindowFor(string limitsUnit) =>
        string.Equals(limitsUnit, "Hour", StringComparison.Ordinal)
            ? TimeSpan.FromHours(1)
            : TimeSpan.FromHours(24);

    /// <summary>
    /// How many hits of <paramref name="kind"/> <paramref name="sourceName"/> has made inside
    /// <paramref name="window"/>, summing RepeatCount. See the type doc for why summing rather than
    /// counting is the correctness property here.
    /// </summary>
    public async Task<int> CountAsync(
        string sourceName,
        EventKind kind,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var since = _timeProvider.GetUtcNow() - window;

        // Kind and source name translate to SQL; the OccurredAt comparison does NOT on this provider
        // (EF Core throws rather than degrading for a DateTimeOffset — the same limitation
        // EventRepository.QueryAsync, PruneAsync and MaintenanceJob all document), so the two
        // timestamp columns and the count are projected and compared in memory. No EventEntry is
        // materialized.
        var candidates = await _dbContext.Events
            .AsNoTracking()
            .Where(e => e.Kind == kind && e.SourceDisplayName == sourceName)
            .Select(e => new { e.OccurredAt, e.LastRepeatedAt, e.RepeatCount })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A FOLDED ROW IS MEASURED FROM ITS LAST ACTIVITY, not its first. A row that started before
        // the window but is STILL REPEATING inside it represents hits that were genuinely spent
        // inside the window, and ageing it off OccurredAt would discard the whole accumulated
        // RepeatCount the moment the first sighting aged out — which is precisely the burst a busy
        // indexer produces. This mirrors how EventRepository.PruneAsync and the coalescing window
        // both measure age, and for the same reason.
        return candidates
            .Where(e => (e.LastRepeatedAt ?? e.OccurredAt) >= since)
            .Sum(e => e.RepeatCount);
    }

    /// <summary>
    /// Whether <paramref name="source"/> has query budget left for one more search.
    /// <c>null</c> <see cref="Source.QueryLimit"/> means unlimited and always answers true.
    /// </summary>
    public Task<bool> HasQueryBudgetAsync(Source source, CancellationToken cancellationToken = default)
        => HasBudgetAsync(source, source?.QueryLimit, EventKind.SourceQueryHit, cancellationToken);

    /// <summary>
    /// Whether <paramref name="source"/> has grab budget left for one more download.
    /// <c>null</c> <see cref="Source.GrabLimit"/> means unlimited and always answers true.
    /// </summary>
    public Task<bool> HasGrabBudgetAsync(Source source, CancellationToken cancellationToken = default)
        => HasBudgetAsync(source, source?.GrabLimit, EventKind.SourceGrabHit, cancellationToken);

    private async Task<bool> HasBudgetAsync(
        Source source,
        int? limit,
        EventKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (limit is not { } cap)
        {
            // NULL IS UNLIMITED. Written as a pattern match on the nullable rather than as
            // `(limit ?? 0)` or `limit.GetValueOrDefault()` precisely because those two spellings are
            // the defect: each silently turns "no limit configured" into "a limit of zero" and
            // disables every unconfigured indexer on its first search.
            return true;
        }

        var used = await CountAsync(source.DisplayName, kind, WindowFor(source.LimitsUnit), cancellationToken)
            .ConfigureAwait(false);

        // Strictly less than: at exactly the cap the allowance is spent, so the NEXT call would
        // exceed it. A limit of zero therefore always answers false, which is the state an operator
        // sets when they mean "do not query this indexer" — the distinction from null above.
        return used < cap;
    }
}
