using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Arbitarr.Data.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-x7w8.10: proves the budget arithmetic against a REAL SQLite database and REAL folded event
/// rows. The defect this whole file exists to catch is a count that sums ROWS rather than
/// <see cref="EventEntry.RepeatCount"/> — a bug that is silent, because the number it produces is
/// merely low and never wrong-looking.
/// </summary>
public sealed class SourceApiHitCounterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new("arr-searcher-source-api-hit-counter-test");

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

    private static SourceApiHitCounter CreateCounter(ArbitarrDbContext context)
        => new(context, new FakeTimeProvider(Now));

    private static Source CreateSource(
        string displayName = "indexer",
        int? queryLimit = null,
        int? grabLimit = null,
        string limitsUnit = "Day") => new()
        {
            Kind = "Newznab",
            DisplayName = displayName,
            BaseUrl = "http://indexer.example.invalid",
            QueryLimit = queryLimit,
            GrabLimit = grabLimit,
            LimitsUnit = limitsUnit,
        };

    /// <summary>
    /// Plants one hit row carrying <paramref name="repeatCount"/>, exactly as coalescing would leave
    /// it: OccurredAt is when the burst started, LastRepeatedAt when it last recurred.
    /// </summary>
    private static EventEntry PlantHit(
        ArbitarrDbContext context,
        string sourceName,
        EventKind kind,
        int repeatCount,
        DateTimeOffset occurredAt,
        DateTimeOffset? lastRepeatedAt = null)
    {
        var entry = new EventEntry
        {
            Kind = kind,
            OccurredAt = occurredAt,
            Summary = "Queried an upstream source",
            SourceDisplayName = sourceName,
            RepeatCount = repeatCount,
            LastRepeatedAt = lastRepeatedAt,
        };

        context.Events.Add(entry);
        return entry;
    }

    /// <summary>
    /// THE ASSERTION THAT CATCHES ROW-COUNTING, and the single most likely defect in this bead. One
    /// row carrying a RepeatCount of 7 represents seven API hits; a count of rows answers 1 and
    /// would let Arbitarr make seven times its permitted number of calls while reporting a budget
    /// barely touched.
    ///
    /// <para>The magnitude matters: 7 against 1 is unmistakable, where a RepeatCount of 2 would leave
    /// an off-by-one implementation looking plausible.</para>
    /// </summary>
    [Fact]
    public async Task A_folded_row_counts_as_every_hit_it_represents_rather_than_as_one()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 7, occurredAt: Now.AddMinutes(-10));
        await context.SaveChangesAsync();

        var count = await CreateCounter(context)
            .CountAsync("indexer", EventKind.SourceQueryHit, TimeSpan.FromHours(24));

        Assert.Equal(7, count);
    }

    /// <summary>Several folded rows sum, rather than the largest winning or the first being taken.</summary>
    [Fact]
    public async Task Folded_rows_sum_their_repeat_counts_across_rows()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 4, occurredAt: Now.AddMinutes(-30));
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 3, occurredAt: Now.AddMinutes(-10));
        await context.SaveChangesAsync();

        var count = await CreateCounter(context)
            .CountAsync("indexer", EventKind.SourceQueryHit, TimeSpan.FromHours(24));

        Assert.Equal(7, count);
    }

    /// <summary>
    /// A row that STARTED before the window but is still repeating inside it counts, because those
    /// repeats are hits genuinely spent inside the window. Ageing off OccurredAt would discard the
    /// whole accumulated RepeatCount the moment the first sighting aged out — which is precisely the
    /// burst a busy indexer produces.
    /// </summary>
    [Fact]
    public async Task A_row_that_started_before_the_window_but_still_repeats_inside_it_counts()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(
            context,
            "indexer",
            EventKind.SourceQueryHit,
            repeatCount: 5,
            occurredAt: Now.AddHours(-3),
            lastRepeatedAt: Now.AddMinutes(-5));
        await context.SaveChangesAsync();

        var count = await CreateCounter(context)
            .CountAsync("indexer", EventKind.SourceQueryHit, TimeSpan.FromHours(1));

        Assert.Equal(5, count);
    }

    /// <summary>A row entirely outside the window does not count.</summary>
    [Fact]
    public async Task A_row_wholly_outside_the_window_does_not_count()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 9, occurredAt: Now.AddHours(-3));
        await context.SaveChangesAsync();

        var count = await CreateCounter(context)
            .CountAsync("indexer", EventKind.SourceQueryHit, TimeSpan.FromHours(1));

        Assert.Equal(0, count);
    }

    /// <summary>Hits belonging to another source are not counted against this one.</summary>
    [Fact]
    public async Task Hits_are_counted_per_source()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "mine", EventKind.SourceQueryHit, repeatCount: 2, occurredAt: Now.AddMinutes(-5));
        PlantHit(context, "theirs", EventKind.SourceQueryHit, repeatCount: 40, occurredAt: Now.AddMinutes(-5));
        await context.SaveChangesAsync();

        Assert.Equal(2, await CreateCounter(context)
            .CountAsync("mine", EventKind.SourceQueryHit, TimeSpan.FromHours(24)));
    }

    /// <summary>
    /// Query hits and grab hits are counted separately: a grab must not spend the query allowance.
    /// </summary>
    [Fact]
    public async Task Query_hits_and_grab_hits_are_counted_separately()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 3, occurredAt: Now.AddMinutes(-5));
        PlantHit(context, "indexer", EventKind.SourceGrabHit, repeatCount: 11, occurredAt: Now.AddMinutes(-5));
        await context.SaveChangesAsync();

        var counter = CreateCounter(context);

        Assert.Equal(3, await counter.CountAsync("indexer", EventKind.SourceQueryHit, TimeSpan.FromHours(24)));
        Assert.Equal(11, await counter.CountAsync("indexer", EventKind.SourceGrabHit, TimeSpan.FromHours(24)));
    }

    /// <summary>
    /// THE HOUR WINDOW AND THE DAY WINDOW ARE DIFFERENT WINDOWS, asserted per unit against one
    /// fixture. The same rows produce different counts, which is what proves LimitsUnit is being
    /// read rather than a single window being hardcoded.
    /// </summary>
    [Theory]
    [InlineData("Hour", 2)]
    [InlineData("Day", 6)]
    public async Task The_window_counted_over_is_the_one_the_limits_unit_names(
        string limitsUnit,
        int expected)
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 2, occurredAt: Now.AddMinutes(-30));
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 4, occurredAt: Now.AddHours(-5));
        await context.SaveChangesAsync();

        var count = await CreateCounter(context).CountAsync(
            "indexer",
            EventKind.SourceQueryHit,
            SourceApiHitCounter.WindowFor(limitsUnit));

        Assert.Equal(expected, count);
    }

    /// <summary>
    /// NULL MEANS UNLIMITED. Half of the pair this nullable column exists for — on its own it proves
    /// nothing, because an implementation that never skipped anything would also pass it. Read it
    /// with the zero case below.
    /// </summary>
    [Fact]
    public async Task A_source_with_a_null_query_limit_always_has_budget_however_many_hits_it_has_made()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 100_000, occurredAt: Now.AddMinutes(-1));
        await context.SaveChangesAsync();

        Assert.True(await CreateCounter(context)
            .HasQueryBudgetAsync(CreateSource(queryLimit: null)));
    }

    /// <summary>
    /// ZERO MEANS ZERO. The other half of the pair: a limit of zero is an operator saying "do not
    /// query this indexer", and it is refused even with no hits recorded at all. Together with the
    /// null case above, this is what makes the distinction observable — an implementation that
    /// coalesced null to zero fails the first, and one that coalesced zero to null fails this one.
    /// </summary>
    [Fact]
    public async Task A_source_with_a_zero_query_limit_never_has_budget_even_with_no_hits_recorded()
    {
        using var context = await CreateMigratedContextAsync();

        Assert.False(await CreateCounter(context)
            .HasQueryBudgetAsync(CreateSource(queryLimit: 0)));
    }

    /// <summary>The same null/zero pair for grabs, which read a different column.</summary>
    [Fact]
    public async Task A_null_grab_limit_is_unlimited_and_a_zero_grab_limit_is_not()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceGrabHit, repeatCount: 500, occurredAt: Now.AddMinutes(-1));
        await context.SaveChangesAsync();

        var counter = CreateCounter(context);

        Assert.True(await counter.HasGrabBudgetAsync(CreateSource(grabLimit: null)));
        Assert.False(await counter.HasGrabBudgetAsync(CreateSource(grabLimit: 0)));
    }

    /// <summary>
    /// The budget is spent AT the cap, not one call past it — and a FOLDED row is what spends it,
    /// which ties the boundary rule to the RepeatCount rule. A row-counting implementation would
    /// read 1 here and report budget remaining against a limit of 5.
    /// </summary>
    [Fact]
    public async Task A_folded_row_that_reaches_the_cap_exhausts_the_budget()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 5, occurredAt: Now.AddMinutes(-5));
        await context.SaveChangesAsync();

        Assert.False(await CreateCounter(context)
            .HasQueryBudgetAsync(CreateSource(queryLimit: 5)));
    }

    /// <summary>One hit below the cap, budget remains — the other side of the boundary above.</summary>
    [Fact]
    public async Task A_source_one_hit_below_its_cap_still_has_budget()
    {
        using var context = await CreateMigratedContextAsync();
        PlantHit(context, "indexer", EventKind.SourceQueryHit, repeatCount: 4, occurredAt: Now.AddMinutes(-5));
        await context.SaveChangesAsync();

        Assert.True(await CreateCounter(context)
            .HasQueryBudgetAsync(CreateSource(queryLimit: 5)));
    }

    /// <summary>
    /// END-TO-END THROUGH THE REAL REPOSITORY, so the count is proved against rows FOLDED by the
    /// production code path rather than against a fixture that merely resembles one. This is the
    /// test that would catch the folding opt-in being removed from MayCoalesce as well as the
    /// arithmetic being wrong: five identical writes produce one row, and the count must still be 5.
    /// </summary>
    [Fact]
    public async Task Five_real_hit_writes_fold_to_one_row_and_still_count_as_five()
    {
        using var context = await CreateMigratedContextAsync();
        var repository = new EventRepository(context, new FakeTimeProvider(Now));

        for (var i = 0; i < 5; i++)
        {
            await repository.AddAsync(
                EventKind.SourceQueryHit,
                "Queried an upstream source",
                reason: null,
                sourceDisplayName: "indexer",
                detail: null,
                CancellationToken.None);
        }

        // The positive control for this test: if these writes had NOT folded, the count below would
        // be 5 by row count alone and would prove nothing about RepeatCount. Asserting one row first
        // is what makes the 5 that follows evidence of summing.
        using var reader = CreateContext();
        var row = Assert.Single(await reader.Events.Where(e => e.Kind == EventKind.SourceQueryHit).ToListAsync());
        Assert.Equal(5, row.RepeatCount);

        Assert.Equal(
            5,
            await CreateCounter(context).CountAsync("indexer", EventKind.SourceQueryHit, TimeSpan.FromHours(24)));
    }
}
