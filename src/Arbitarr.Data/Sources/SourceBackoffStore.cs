using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Sources;

/// <summary>
/// arb-x7w8.10: EF Core-backed reader/writer over the <see cref="SourceBackoffState"/> table, and
/// the single place the escalation rules are APPLIED.
///
/// <para>The connection string is NOT formatted here — this takes an
/// <see cref="ArbitarrDbContext"/> whose options the composition root built from
/// <c>DatabaseConnectionStrings</c> (docs/standards/data.md), the same posture
/// <c>DownloadRefusalStore</c> takes. That also pins WHICH database this is: the application
/// database <c>arbitarr.db</c>, never <c>arbitarr-logs.db</c>, which has no EF migrations at all.</para>
///
/// <para><b>THE STATE IS READ FROM THE ROW, NEVER FROM A CACHE.</b> That is what makes it survive a
/// restart, which is the entire reason the table exists — a process that rehydrates nothing starts
/// every source at level zero and re-enables a source whose key is known to be rejected. There is
/// deliberately no in-memory mirror to keep in step with it.</para>
/// </summary>
public sealed class SourceBackoffStore
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;

    /// <param name="startedAt">
    /// When the host started, which anchors <see cref="SourceBackoffPolicy.StartupGraceWindow"/>.
    /// Passed in rather than captured from <paramref name="timeProvider"/> at construction because
    /// this type is SCOPED — constructed per request, long after the host started — so capturing it
    /// here would make the window restart on every request and suppress escalation forever.
    /// </param>
    public SourceBackoffStore(
        ArbitarrDbContext dbContext,
        TimeProvider timeProvider,
        DateTimeOffset startedAt)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startedAt = startedAt;
    }

    /// <summary>The stored state for <paramref name="sourceName"/>, or null when it has none.</summary>
    public Task<SourceBackoffState?> GetAsync(string sourceName, CancellationToken cancellationToken = default)
        => _dbContext.SourceBackoffStates
            .SingleOrDefaultAsync(s => s.SourceName == sourceName, cancellationToken);

    /// <summary>
    /// arb-x7w8.11: every stored state, keyed by source name — the LIST read the per-source operator
    /// surface needs, and the counterpart <c>SourceHealthRepository.LoadAllAsync</c> has had since
    /// the breaker shipped.
    ///
    /// <para><b>It returns the ROWS, not a verdict, and that is the point.</b>
    /// <see cref="IsCallableAsync"/> collapses a state to a bool, which discards exactly the
    /// distinction the surface exists to draw: backing off and permanently disabled are both
    /// "not callable" and are two entirely different things to tell an operator (see
    /// <see cref="SourceBackoffState"/>'s "Three states" paragraph). A caller wanting the verdict
    /// still asks <see cref="IsCallableAsync"/>; a caller wanting to SAY WHY reads these.</para>
    ///
    /// <para>One query rather than N per-name <see cref="GetAsync"/> calls. The table holds exactly
    /// one row per source and is bounded by set membership (see the entity's "cannot grow without
    /// bound" note), so loading it whole costs one round trip where the keyed form costs one per
    /// configured source on every render of the surface.</para>
    ///
    /// <para>A source with no row is simply ABSENT from the dictionary rather than present with a
    /// blank state. A source that has never been called has recorded nothing, which the caller reads
    /// the same way <see cref="IsCallableAsync"/> reads a null state — as no impediment — but
    /// inventing a row here would make "never observed" indistinguishable from "observed healthy".
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SourceBackoffState>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        // AsNoTracking because this is a read for projection: the tracked form would put every
        // source's row into the change tracker of a context RecordOutcomeAsync also writes through.
        var states = await _dbContext.SourceBackoffStates
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordinal, matching both the unique index's own comparison and GetAsync's equality above. A
        // case-insensitive dictionary would try to merge two rows the table considers distinct and
        // throw on the duplicate key rather than mis-report.
        return states.ToDictionary(s => s.SourceName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="sourceName"/> may be called right now.
    ///
    /// <para>A permanent disable wins outright and is checked FIRST: it is not a period that elapses,
    /// so consulting <see cref="SourceBackoffState.DisabledUntil"/> first would let a permanently
    /// disabled source become callable again the moment an unrelated backoff expired.</para>
    /// </summary>
    public async Task<bool> IsCallableAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var state = await GetAsync(sourceName, cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            return true;
        }

        if (state.IsPermanentlyDisabled)
        {
            return false;
        }

        return state.DisabledUntil is not { } until || _timeProvider.GetUtcNow() >= until;
    }

    /// <summary>
    /// Applies <paramref name="outcome"/> to <paramref name="sourceName"/>'s state and returns the
    /// stored row. This is where all three escalation rules live, and each is a decision the bead
    /// records a rejected alternative for:
    ///
    /// <list type="bullet">
    /// <item><description><b>Success resets the level to ZERO</b>, and clears both the hold-off and
    /// the permanent flag. Not a decrement: a recovered source is recovered, not left carrying
    /// levels of suspicion it has just disproved.</description></item>
    /// <item><description><b>An authentication failure sets
    /// <see cref="SourceBackoffState.IsPermanentlyDisabled"/> and does NOT touch
    /// <see cref="SourceBackoffState.DisabledLevel"/>.</b> It bypasses escalation entirely — see
    /// that property's doc for why escalating one is worse than useless. It is NOT suppressed by the
    /// startup grace window either: a rejected key is not a symptom of a cold start.</description></item>
    /// <item><description><b>A transient failure escalates one level</b>, saturating at
    /// <see cref="SourceBackoffPolicy.MaxLevel"/> — unless the host started less than
    /// <see cref="SourceBackoffPolicy.StartupGraceWindow"/> ago, in which case the outcome is
    /// recorded but escalation is suppressed, so a restart does not disable every source at
    /// once.</description></item>
    /// <item><description><b><see cref="SourceCallOutcome.NotAttempted"/> writes NOTHING AT ALL</b>
    /// and creates no row — the call never reached upstream, so it is evidence of neither health nor
    /// fault. See that member for why filing it as either is a defect rather than a simplification.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <returns>
    /// The stored state, or null when the outcome was <see cref="SourceCallOutcome.NotAttempted"/>
    /// and no row existed to begin with.
    /// </returns>
    public async Task<SourceBackoffState?> RecordOutcomeAsync(
        string sourceName,
        SourceCallOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        if (outcome == SourceCallOutcome.NotAttempted)
        {
            // NOTHING IS WRITTEN, not even LastOutcome, and no row is created. The call never
            // happened, so there is no observation to record — and the two spellings that look
            // tidier are both wrong: filing it as a success would clear a permanent disable (an open
            // breaker would silently re-enable a source with a rejected key), and filing it as a
            // failure would escalate a source for a refusal Arbitarr itself issued. Returning the
            // untouched stored row, if any, keeps the caller able to see the state it is in.
            return await GetAsync(sourceName, cancellationToken).ConfigureAwait(false);
        }

        var now = _timeProvider.GetUtcNow();

        var state = await GetAsync(sourceName, cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            state = new SourceBackoffState { SourceName = sourceName };
            _dbContext.SourceBackoffStates.Add(state);
        }

        state.LastOutcome = outcome.ToString();
        state.UpdatedAt = now;

        switch (outcome)
        {
            case SourceCallOutcome.Success:
                state.DisabledLevel = 0;
                state.DisabledUntil = null;
                state.IsPermanentlyDisabled = false;
                break;

            case SourceCallOutcome.AuthenticationFailure:
                state.IsPermanentlyDisabled = true;
                state.DisabledUntil = null;
                break;

            case SourceCallOutcome.TransientFailure:
                // The grace window suppresses the ESCALATION, not the record: LastOutcome and
                // UpdatedAt above are still written, so an operator looking at the row during a cold
                // start sees that the source is failing even though nothing is being held off yet.
                if (now - _startedAt >= SourceBackoffPolicy.StartupGraceWindow)
                {
                    state.DisabledLevel = Math.Min(state.DisabledLevel + 1, SourceBackoffPolicy.MaxLevel);
                    state.DisabledUntil = now + SourceBackoffPolicy.PeriodFor(state.DisabledLevel);
                }

                break;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return state;
    }
}
