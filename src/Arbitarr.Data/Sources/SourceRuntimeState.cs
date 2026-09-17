using Arbitarr.Data.Entities;

namespace Arbitarr.Data.Sources;

/// <summary>
/// arb-x7w8.11: the four states a configured source can be in as far as an operator is concerned,
/// as a closed enum the wire carries by NAME.
///
/// <para><b>These are four different things to tell an operator and must not be collapsed into one
/// "unavailable".</b> <see cref="SourceBackoffState"/>'s own doc states the reason: collapsing them
/// "reports a broken key as a temporary pause and removes the signal to go and fix it". The house
/// precedent is <c>SourceProbeOutcome</c>'s five distinct probe outcomes — the comment above
/// <c>OUTCOME_LABELS</c> in <c>Sources.tsx</c> spells out why a single red verdict makes the control
/// decorative — and the reasoning transfers exactly: the fixes differ. A budgeted source needs a
/// higher limit or patience, a backing-off one needs nothing at all, and a permanently disabled one
/// needs a human to replace a credential.</para>
///
/// <para><b>A closed enum, deliberately, and no free-text sibling.</b> Like
/// <c>SourceProbeOutcome</c> this carries no string field, so no branch can interpolate an upstream
/// body, an exception message or a credential into what the surface renders. <c>LastOutcome</c>
/// travels alongside it as a <see cref="SourceCallOutcome"/> NAME for the same reason.</para>
/// </summary>
public enum SourceRuntimeState
{
    /// <summary>Nothing is holding this source back: it is callable and within its allowance.</summary>
    Healthy,

    /// <summary>
    /// The allowance for the current rolling window is spent, so searches skip this source until the
    /// window rolls. Derived from EVENT COUNTS rather than from any stored flag — see
    /// <see cref="SourceBackoffState"/>'s note that budgeted "is not represented here at all".
    /// </summary>
    Budgeted,

    /// <summary>
    /// A transient fault has held this source off until <see cref="SourceBackoffState.DisabledUntil"/>,
    /// which is still in the future. It clears itself, so it is NOT a health item.
    /// </summary>
    BackingOff,

    /// <summary>
    /// Authentication failed, so this source is disabled until a human replaces the credential.
    /// Never clears on its own, which is what makes it a blocking health item.
    /// </summary>
    PermanentlyDisabled,
}

/// <summary>
/// arb-x7w8.11: one source's runtime state as the admin sources surface renders it — the JOIN of the
/// durable backoff ROW with the event-derived budget COUNTS, which no single existing type holds
/// because the three states come from two different mechanisms.
/// </summary>
/// <param name="State">
/// The derived state. See <see cref="SourceRuntimeStateReader"/> for the precedence that produces it,
/// which is not free to reorder.
/// </param>
/// <param name="DisabledUntil">
/// When a transient hold-off expires, or null. <b>Non-null does NOT mean "backing off"</b>: a value
/// in the PAST means the backoff has elapsed and is retained only because the level it was reached
/// at is still live information (see <see cref="SourceBackoffState.DisabledUntil"/>). It is projected
/// raw so the surface can say how long is left, and <paramref name="State"/> — not this field — is
/// what says whether anything is being held off at all.
/// </param>
/// <param name="DisabledLevel">How far transient escalation has climbed. Zero means not escalated.</param>
/// <param name="LastOutcome">
/// The last observed <see cref="SourceCallOutcome"/> as its enum NAME, or null before any outcome was
/// recorded. An enum name and never upstream text: a free-text <c>lastError</c> here would weaken the
/// structural no-secrets guarantee <c>SourceResponse</c> holds by having no field able to carry one.
/// </param>
/// <param name="QueriesUsed">Query hits spent inside the current rolling window, summing RepeatCount.</param>
/// <param name="GrabsUsed">Grab hits spent inside the current rolling window, summing RepeatCount.</param>
public sealed record SourceRuntimeStatus(
    SourceRuntimeState State,
    DateTimeOffset? DisabledUntil,
    int DisabledLevel,
    string? LastOutcome,
    int QueriesUsed,
    int GrabsUsed);

/// <summary>
/// arb-x7w8.11: builds a <see cref="SourceRuntimeStatus"/> per source by joining
/// <see cref="SourceBackoffStore"/>'s rows with <see cref="SourceApiHitCounter"/>'s window counts.
///
/// <para><b>Why a join at all.</b> The three non-healthy states come from two different mechanisms
/// and there was no type holding both: backing off and permanently disabled are columns on a durable
/// row, while budgeted is derived from counted events and is deliberately "not represented [on the
/// row] at all". Neither half alone can answer what an operator is looking at.</para>
///
/// <para><b>Reads, never writes.</b> Nothing here records an outcome or touches the events table.
/// The state is read from the row exactly as <see cref="SourceBackoffStore"/>'s type doc requires —
/// there is no in-memory mirror here either, and adding one would be the defect that doc names.</para>
///
/// <para><b>Cost, and why this is not on a poll.</b> <see cref="SourceApiHitCounter.CountAsync"/>
/// filters its time window IN MEMORY (EF Core cannot translate the <c>DateTimeOffset</c> comparison
/// on SQLite), so N sources costs 2N reads. arb-15u3's <c>(Kind, SourceDisplayName)</c> index bounds
/// each of those scans to one source's rows, which is what makes this affordable — but it is still
/// work per render, so this hangs off the admin sources LIST the operator already fetches on demand
/// and is deliberately not given a polling interval of its own.</para>
/// </summary>
public sealed class SourceRuntimeStateReader
{
    private readonly SourceBackoffStore _backoff;
    private readonly SourceApiHitCounter _hits;
    private readonly TimeProvider _timeProvider;

    public SourceRuntimeStateReader(
        SourceBackoffStore backoff,
        SourceApiHitCounter hits,
        TimeProvider timeProvider)
    {
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _hits = hits ?? throw new ArgumentNullException(nameof(hits));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// The runtime status of every source in <paramref name="sources"/>, keyed by display name.
    ///
    /// <para>The backoff rows are loaded ONCE for the whole set rather than per source, which is the
    /// reason <see cref="SourceBackoffStore.GetAllAsync"/> exists; the hit counts genuinely are per
    /// source and per kind, because that is the shape of the question.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SourceRuntimeStatus>> GetAllAsync(
        IReadOnlyList<Source> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var states = await _backoff.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, SourceRuntimeStatus>(sources.Count, StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var window = SourceApiHitCounter.WindowFor(source.LimitsUnit);

            var queriesUsed = await _hits
                .CountAsync(source.DisplayName, EventKind.SourceQueryHit, window, cancellationToken)
                .ConfigureAwait(false);
            var grabsUsed = await _hits
                .CountAsync(source.DisplayName, EventKind.SourceGrabHit, window, cancellationToken)
                .ConfigureAwait(false);

            states.TryGetValue(source.DisplayName, out var state);

            result[source.DisplayName] = new SourceRuntimeStatus(
                State: Derive(
                    state,
                    source.QueryLimit,
                    queriesUsed,
                    source.GrabLimit,
                    grabsUsed,
                    _timeProvider.GetUtcNow()),
                DisabledUntil: state?.DisabledUntil,
                DisabledLevel: state?.DisabledLevel ?? 0,
                LastOutcome: state?.LastOutcome,
                QueriesUsed: queriesUsed,
                GrabsUsed: grabsUsed);
        }

        return result;
    }

    /// <summary>
    /// The state-derivation rule, and <b>its ORDER is load-bearing rather than stylistic</b>.
    ///
    /// <para>Permanent disable is checked FIRST, for the reason
    /// <see cref="SourceBackoffStore.IsCallableAsync"/> states verbatim: it is not a period that
    /// elapses, so consulting <paramref name="state"/>'s <c>DisabledUntil</c> first would let a
    /// permanently disabled source read as healthy the moment an unrelated backoff expired. Backing
    /// off is checked SECOND and only against an instant in the FUTURE — a past
    /// <c>DisabledUntil</c> means the hold-off elapsed and the source is callable again, so rendering
    /// any non-null value as "backing off" would show a recovered source as broken indefinitely.
    /// Budgeted is checked LAST because it is the mildest and the most transient: a source that is
    /// also failing has a more urgent thing to say about itself.</para>
    ///
    /// <para><b>BOTH allowances are read, and EITHER one spent is Budgeted.</b> The refusal this
    /// renders is <see cref="Arbitarr.Data.Entities.EventKind"/>-scoped upstream: the budget gate
    /// asks the QUERY allowance for a search and the GRAB allowance for a download, so a source
    /// whose grabs are spent while its queries are not really does refuse every download. Deriving
    /// from the query pair alone rendered that source Healthy, which is the one reading an operator
    /// watching downloads fail cannot act on. One badge still, and deliberately: the two allowances
    /// share a window and a remedy, so splitting the badge would ask the operator to distinguish
    /// between two states with the same fix while the per-kind tallies are already on the row.</para>
    /// </summary>
    /// <param name="queryLimit">
    /// The source's query cap. <b>Null is UNLIMITED and is not zero</b> (see
    /// <see cref="Source.QueryLimit"/>): an unconfigured limit can never be budgeted, whereas a limit
    /// of zero always is. Pattern-matched rather than written <c>limit ?? 0</c> precisely because
    /// that spelling is the defect — it would report every indexer whose limit an operator never set
    /// as budgeted from its first search.
    /// </param>
    /// <param name="grabLimit">
    /// The source's grab cap, under the identical null-is-unlimited rule as
    /// <paramref name="queryLimit"/> and for the identical reason (see <see cref="Source.GrabLimit"/>).
    /// </param>
    /// <remarks>
    /// <para><c>public</c>, not <c>internal</c>: there is no <c>InternalsVisibleTo</c> from this
    /// project to the test assembly, so an <c>internal</c> modifier would leave the precedence above
    /// — the part of this type most worth pinning and the part with no persistence in it — reachable
    /// only through a database round trip per case. Static and pure, so exposing it adds no state
    /// and no second way to reach the store.</para>
    /// </remarks>
    public static SourceRuntimeState Derive(
        SourceBackoffState? state,
        int? queryLimit,
        int queriesUsed,
        int? grabLimit,
        int grabsUsed,
        DateTimeOffset now)
    {
        if (state is { IsPermanentlyDisabled: true })
        {
            return SourceRuntimeState.PermanentlyDisabled;
        }

        if (state?.DisabledUntil is { } until && now < until)
        {
            return SourceRuntimeState.BackingOff;
        }

        // Greater than or equal, matching SourceApiHitCounter's `used < cap` budget test exactly: at
        // the cap the allowance is spent, so the next call would exceed it. Deriving the same
        // boundary twice from one rule is why this comparison is written to mirror that one. The
        // SAME mirror applies to each allowance, because upstream the same `used < cap` answers both
        // HasQueryBudgetAsync and HasGrabBudgetAsync.
        if (IsSpent(queryLimit, queriesUsed) || IsSpent(grabLimit, grabsUsed))
        {
            return SourceRuntimeState.Budgeted;
        }

        return SourceRuntimeState.Healthy;
    }

    /// <summary>
    /// One allowance's spent test, written once so the query and grab arms cannot drift apart: a
    /// null cap is unlimited and can never be spent, and a configured cap is spent at the cap
    /// rather than past it. The <c>is { } cap</c> pattern is the whole point of extracting it: a
    /// second hand-written copy is where a <c>?? 0</c> would eventually appear in only one of them.
    /// </summary>
    private static bool IsSpent(int? limit, int used) => limit is { } cap && used >= cap;
}
