using Arbitarr.Data.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-x7w8.10: proves the escalation rules against a REAL SQLite database. Every assertion that
/// claims something survives a restart reads it back through a SEPARATE context, because the defect
/// the table exists to fix is precisely that none of it survived the process.
/// </summary>
public sealed class SourceBackoffStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Past <see cref="SourceBackoffPolicy.StartupGraceWindow"/>, so escalation is live. Every test
    /// that is not ABOUT the grace window uses this, so none of them passes merely because the
    /// window was open.
    /// </summary>
    private static readonly DateTimeOffset AfterGrace = Start + TimeSpan.FromMinutes(30);

    private readonly SqliteTestDatabase _database = new("arr-searcher-source-backoff-store-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    private async Task<ArbitarrDbContext> CreateMigratedContextAsync()
    {
        var context = CreateContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static SourceBackoffStore CreateStore(ArbitarrDbContext context, DateTimeOffset now)
        => new(context, new FakeTimeProvider(now), Start);

    /// <summary>
    /// THE PERIOD TABLE, ASSERTED PER LEVEL. Six separate cases rather than one "it got longer"
    /// assertion: a monotonicity check passes against any increasing sequence, so it would survive a
    /// wholesale replacement of the figures with different ones. These are the figures.
    /// </summary>
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(5, 180)]
    public async Task Escalation_holds_the_source_off_for_the_period_its_level_names(
        int failures,
        int expectedMinutes)
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, AfterGrace);

        for (var i = 0; i < failures; i++)
        {
            await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);
        }

        var state = await store.GetAsync("indexer");

        Assert.NotNull(state);
        Assert.Equal(failures, state.DisabledLevel);
        Assert.Equal(AfterGrace + TimeSpan.FromMinutes(expectedMinutes), state.DisabledUntil);
    }

    /// <summary>Level 0 is not a hold-off at all: a source that has not failed is not held off.</summary>
    [Fact]
    public async Task A_source_that_has_never_failed_is_callable_and_has_no_hold_off()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, AfterGrace);

        Assert.True(await store.IsCallableAsync("indexer"));
    }

    /// <summary>
    /// Escalation SATURATES at the last period rather than doubling forever, so a source broken all
    /// day is still retried every three hours.
    /// </summary>
    [Fact]
    public async Task Escalation_saturates_at_the_longest_period()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, AfterGrace);

        for (var i = 0; i < 12; i++)
        {
            await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);
        }

        var state = await store.GetAsync("indexer");

        Assert.NotNull(state);
        Assert.Equal(SourceBackoffPolicy.MaxLevel, state.DisabledLevel);
        Assert.Equal(AfterGrace + TimeSpan.FromMinutes(180), state.DisabledUntil);
    }

    /// <summary>
    /// SUCCESS RESETS TO ZERO, NOT TO LEVEL MINUS ONE. This is the mutation a decrement survives:
    /// after five failures a decrementing implementation leaves level 4, which is still a 60-minute
    /// hold-off on a source that has just demonstrably recovered. Asserting the exact zero is what
    /// makes the difference visible.
    /// </summary>
    [Fact]
    public async Task Success_resets_the_level_to_zero_rather_than_decrementing_it()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, AfterGrace);

        for (var i = 0; i < 5; i++)
        {
            await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);
        }

        Assert.Equal(5, (await store.GetAsync("indexer"))!.DisabledLevel);

        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.Success);

        var state = await store.GetAsync("indexer");

        Assert.NotNull(state);
        Assert.Equal(0, state.DisabledLevel);
    }

    /// <summary>Success also clears the hold-off, so a recovered source is callable immediately.</summary>
    [Fact]
    public async Task Success_clears_the_hold_off()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, AfterGrace);

        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);
        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.Success);

        Assert.Null((await store.GetAsync("indexer"))!.DisabledUntil);
    }

    /// <summary>
    /// AN AUTH FAILURE SETS THE PERMANENT FLAG AND DOES NOT ESCALATE. Both halves are asserted,
    /// because an implementation that set the flag AND escalated would pass a test of either half
    /// alone while producing exactly the "permanent fault disguised as an intermittent one" that
    /// ADR 0020 rejects.
    /// </summary>
    [Fact]
    public async Task An_authentication_failure_disables_permanently_without_escalating()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, AfterGrace);

        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.AuthenticationFailure);

        var state = await store.GetAsync("indexer");

        Assert.NotNull(state);
        Assert.True(state.IsPermanentlyDisabled);
        Assert.Equal(0, state.DisabledLevel);
    }

    /// <summary>A permanently disabled source is not callable, and no elapsed period changes that.</summary>
    [Fact]
    public async Task A_permanently_disabled_source_stays_uncallable_however_long_passes()
    {
        using var context = await CreateMigratedContextAsync();

        await CreateStore(context, AfterGrace)
            .RecordOutcomeAsync("indexer", SourceCallOutcome.AuthenticationFailure);

        var muchLater = CreateStore(context, AfterGrace + TimeSpan.FromDays(30));

        Assert.False(await muchLater.IsCallableAsync("indexer"));
    }

    /// <summary>
    /// An auth failure is NOT suppressed by the startup grace window. A rejected key is not a
    /// symptom of a cold start, and hiding it for fifteen minutes buys nothing.
    /// </summary>
    [Fact]
    public async Task An_authentication_failure_inside_the_grace_window_still_disables_permanently()
    {
        using var context = await CreateMigratedContextAsync();

        await CreateStore(context, Start + TimeSpan.FromMinutes(1))
            .RecordOutcomeAsync("indexer", SourceCallOutcome.AuthenticationFailure);

        Assert.True((await CreateStore(context, Start).GetAsync("indexer"))!.IsPermanentlyDisabled);
    }

    /// <summary>
    /// THE GRACE WINDOW SUPPRESSES ESCALATION. Without it, the burst of failures every restart
    /// produces disables every configured source at once, exactly when an operator is watching a
    /// dashboard after a deploy.
    /// </summary>
    [Fact]
    public async Task A_transient_failure_inside_the_startup_grace_window_does_not_escalate()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, Start + TimeSpan.FromMinutes(14));

        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);

        var state = await store.GetAsync("indexer");

        Assert.NotNull(state);
        Assert.Equal(0, state.DisabledLevel);
        Assert.Null(state.DisabledUntil);
    }

    /// <summary>
    /// The suppression is the window, not a permanent exemption: once it elapses, escalation
    /// resumes. Asserted separately from the case above because a store that never escalated at all
    /// would pass that one.
    /// </summary>
    [Fact]
    public async Task Escalation_resumes_once_the_grace_window_has_elapsed()
    {
        using var context = await CreateMigratedContextAsync();

        await CreateStore(context, Start + TimeSpan.FromMinutes(14))
            .RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);

        var after = Start + SourceBackoffPolicy.StartupGraceWindow;
        await CreateStore(context, after).RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);

        var state = await CreateStore(context, after).GetAsync("indexer");

        Assert.NotNull(state);
        Assert.Equal(1, state.DisabledLevel);
    }

    /// <summary>
    /// The grace window suppresses the escalation, NOT the record: an operator looking at the row
    /// during a cold start still sees that the source is failing.
    /// </summary>
    [Fact]
    public async Task A_failure_inside_the_grace_window_is_still_recorded_as_the_last_outcome()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, Start + TimeSpan.FromMinutes(2));

        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);

        Assert.Equal(
            nameof(SourceCallOutcome.TransientFailure),
            (await store.GetAsync("indexer"))!.LastOutcome);
    }

    /// <summary>A source inside its hold-off is not callable; past it, it is.</summary>
    [Fact]
    public async Task A_backing_off_source_becomes_callable_once_its_period_elapses()
    {
        using var context = await CreateMigratedContextAsync();

        await CreateStore(context, AfterGrace)
            .RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);

        Assert.False(await CreateStore(context, AfterGrace + TimeSpan.FromMinutes(4)).IsCallableAsync("indexer"));
        Assert.True(await CreateStore(context, AfterGrace + TimeSpan.FromMinutes(6)).IsCallableAsync("indexer"));
    }

    /// <summary>
    /// STATE SURVIVES A RESTART, ASSERTED PER SOURCE AT N=3 — one healthy, one backing off, one
    /// permanently disabled.
    ///
    /// <para>N=3 rather than N=1 is the whole point. "State was rehydrated" passes VACUOUSLY at N=1:
    /// an implementation that read one row and applied it to every source, or that returned a single
    /// global state, would be indistinguishable from a correct one. Three sources in three different
    /// states, each asserted individually, is what makes per-source rehydration observable.</para>
    /// </summary>
    [Fact]
    public async Task Three_sources_in_three_states_each_rehydrate_their_own_state_from_a_new_context()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            var store = CreateStore(context, AfterGrace);
            await store.RecordOutcomeAsync("healthy", SourceCallOutcome.Success);
            await store.RecordOutcomeAsync("backing-off", SourceCallOutcome.TransientFailure);
            await store.RecordOutcomeAsync("no-key", SourceCallOutcome.AuthenticationFailure);
        }

        // A SEPARATE context over the same file: this is the restart.
        using (var context = CreateContext())
        {
            var store = CreateStore(context, AfterGrace + TimeSpan.FromMinutes(1));

            var healthy = await store.GetAsync("healthy");
            Assert.NotNull(healthy);
            Assert.Equal(0, healthy.DisabledLevel);
            Assert.False(healthy.IsPermanentlyDisabled);
            Assert.True(await store.IsCallableAsync("healthy"));

            var backingOff = await store.GetAsync("backing-off");
            Assert.NotNull(backingOff);
            Assert.Equal(1, backingOff.DisabledLevel);
            Assert.False(backingOff.IsPermanentlyDisabled);
            Assert.False(await store.IsCallableAsync("backing-off"));

            var noKey = await store.GetAsync("no-key");
            Assert.NotNull(noKey);
            Assert.True(noKey.IsPermanentlyDisabled);
            Assert.False(await store.IsCallableAsync("no-key"));
        }
    }

    /// <summary>
    /// One row per source, however many outcomes are recorded — the bound that lets this table go
    /// without a prune. A second row would also make the unique index throw, so this pins both.
    /// </summary>
    [Fact]
    public async Task Repeated_outcomes_update_one_row_rather_than_appending()
    {
        using var context = await CreateMigratedContextAsync();
        var store = CreateStore(context, AfterGrace);

        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);
        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.TransientFailure);
        await store.RecordOutcomeAsync("indexer", SourceCallOutcome.Success);

        using var reader = CreateContext();
        Assert.Equal(1, await reader.SourceBackoffStates.CountAsync(s => s.SourceName == "indexer"));
    }
}
