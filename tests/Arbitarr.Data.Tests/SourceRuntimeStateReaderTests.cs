using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-x7w8.11: the state-derivation rule, whose ORDER is load-bearing rather than stylistic.
///
/// <para>These drive <c>Derive</c> directly rather than through a database, because every property
/// worth pinning here is about the PRECEDENCE among four conditions and none of it is about
/// persistence — which <see cref="SourceBackoffStoreTests"/> already covers against real SQLite.</para>
/// </summary>
public sealed class SourceRuntimeStateReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static SourceBackoffState State(
        bool permanentlyDisabled = false,
        DateTimeOffset? disabledUntil = null,
        int level = 0) =>
        new()
        {
            SourceName = "indexer",
            IsPermanentlyDisabled = permanentlyDisabled,
            DisabledUntil = disabledUntil,
            DisabledLevel = level,
        };

    /// <summary>
    /// No row at all is HEALTHY, not an error state — a source that has never been called has
    /// nothing wrong with it.
    /// </summary>
    [Fact]
    public void A_source_with_no_row_and_no_limit_is_healthy()
    {
        Assert.Equal(
            SourceRuntimeState.Healthy,
            SourceRuntimeStateReader.Derive(state: null, limit: null, used: 0, Now));
    }

    /// <summary>
    /// A hold-off still in the future is BACKING OFF. The counterpart to the elapsed case below, so
    /// neither passes against an implementation that ignores the instant entirely.
    /// </summary>
    [Fact]
    public void A_hold_off_in_the_future_is_backing_off()
    {
        Assert.Equal(
            SourceRuntimeState.BackingOff,
            SourceRuntimeStateReader.Derive(
                State(disabledUntil: Now.AddMinutes(5), level: 2),
                limit: null,
                used: 0,
                Now));
    }

    /// <summary>
    /// <b>A <c>DisabledUntil</c> IN THE PAST is HEALTHY, not backing off.</b> The row does not clear
    /// it eagerly — the level it was reached at is still live information until the next outcome
    /// resolves it — so an implementation keyed off the field being non-null would report every
    /// recovered source as held off indefinitely.
    /// </summary>
    [Fact]
    public void A_hold_off_that_has_elapsed_is_healthy_again()
    {
        Assert.Equal(
            SourceRuntimeState.Healthy,
            SourceRuntimeStateReader.Derive(
                State(disabledUntil: Now.AddMinutes(-1), level: 3),
                limit: null,
                used: 0,
                Now));
    }

    /// <summary>
    /// <b>PERMANENT BEATS BACKING OFF even when the backoff has EXPIRED.</b> This is
    /// <c>IsCallableAsync</c>'s documented ordering, and the reason it is documented: a permanent
    /// disable is not a period that elapses, so consulting <c>DisabledUntil</c> first would let a
    /// source with a rejected key read as healthy the moment an unrelated hold-off ran out.
    /// </summary>
    [Fact]
    public void A_permanent_disable_wins_over_an_expired_hold_off()
    {
        Assert.Equal(
            SourceRuntimeState.PermanentlyDisabled,
            SourceRuntimeStateReader.Derive(
                State(permanentlyDisabled: true, disabledUntil: Now.AddMinutes(-1)),
                limit: null,
                used: 0,
                Now));
    }

    /// <summary>Permanent also beats a LIVE hold-off, so the precedence holds on both sides of the clock.</summary>
    [Fact]
    public void A_permanent_disable_wins_over_a_live_hold_off()
    {
        Assert.Equal(
            SourceRuntimeState.PermanentlyDisabled,
            SourceRuntimeStateReader.Derive(
                State(permanentlyDisabled: true, disabledUntil: Now.AddMinutes(5)),
                limit: null,
                used: 0,
                Now));
    }

    /// <summary>
    /// A fault outranks a spent allowance: a source that is failing has a more urgent thing to say
    /// about itself than that its budget is used up.
    /// </summary>
    [Fact]
    public void A_live_hold_off_wins_over_a_spent_budget()
    {
        Assert.Equal(
            SourceRuntimeState.BackingOff,
            SourceRuntimeStateReader.Derive(
                State(disabledUntil: Now.AddMinutes(5)),
                limit: 10,
                used: 10,
                Now));
    }

    /// <summary>
    /// <b>NULL LIMIT IS UNLIMITED AND IS NOT ZERO.</b> An unconfigured limit can never be budgeted,
    /// however many hits have been spent. The <c>limit ?? 0</c> spelling is the defect: it would turn
    /// every indexer whose limit an operator never set into a budgeted one on its first search.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    public void A_null_limit_is_never_budgeted_however_many_hits_were_spent(int used)
    {
        Assert.Equal(
            SourceRuntimeState.Healthy,
            SourceRuntimeStateReader.Derive(state: null, limit: null, used, Now));
    }

    /// <summary>
    /// The budget boundary, per case. UNDER the cap is not budgeted, AT it is, and OVER it is — all
    /// three, because asserting only the at-limit case passes against a constant-returning
    /// implementation and asserting only the under-limit case passes against the inverse.
    ///
    /// <para>The at-limit arm mirrors <c>SourceApiHitCounter</c>'s <c>used &lt; cap</c> exactly: at the
    /// cap the allowance is spent, so the next call would exceed it.</para>
    /// </summary>
    [Theory]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    [InlineData(6, 5, true)]
    // A cap of ZERO is a real configured value meaning "do not query this indexer", and is the
    // state null exists to be distinguishable from. It is budgeted from the very first render.
    [InlineData(0, 0, true)]
    public void The_budget_boundary_is_at_the_cap_not_past_it(int used, int limit, bool expectBudgeted)
    {
        var derived = SourceRuntimeStateReader.Derive(state: null, limit, used, Now);

        Assert.Equal(
            expectBudgeted ? SourceRuntimeState.Budgeted : SourceRuntimeState.Healthy,
            derived);
    }
}
