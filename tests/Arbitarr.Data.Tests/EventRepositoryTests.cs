using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #55 step 1 (foundation shared with #54, plan §2): proves the migration is lossless against an
/// existing populated database, that <see cref="EventRepository"/> validates at the repository
/// boundary (AC24 — reject, never clamp), and that per-kind retention actually prunes decisions on
/// a different clock than operational events (plan §2/AC5). This stage ships no emission and no
/// read API beyond a placeholder listing — nothing here proves events are *produced* by the
/// pipeline/worker; that is the next stage's job.
/// </summary>
public sealed class EventRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public EventRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-events-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task Migration_applies_to_an_existing_populated_database_without_data_loss()
    {
        // Simulate an existing deployment: migrate to the state just before AddEventsTable, write a
        // settings row (stand-in for pre-existing operator data), then migrate the rest of the way
        // (including AddEventsTable) and confirm the earlier row survives untouched.
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            var migrator = context.GetInfrastructure()
                .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("VerdictCacheEntryRewrittenTitle");

            context.Settings.Add(new SettingEntry
            {
                Name = "pre_existing_setting",
                Value = "keep-me",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            await context.Database.MigrateAsync();

            var preserved = await context.Settings.SingleAsync(e => e.Name == "pre_existing_setting");
            Assert.Equal("keep-me", preserved.Value);

            // The new table exists and is queryable (empty, as expected — nothing writes to it automatically).
            var eventCount = await context.Events.CountAsync();
            Assert.Equal(0, eventCount);
        }
    }

    [Fact]
    public async Task AddAsync_persists_a_decision_event()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        var entry = await repository.AddAsync(
            EventKind.Decision,
            summary: "Suppressed a release",
            reason: "Matched blocklist rule 'low-quality-source'",
            sourceDisplayName: null,
            detail: "{\"releaseId\":\"abc123\",\"shadowMode\":true}",
            CancellationToken.None);

        Assert.True(entry.Id > 0);

        var all = await repository.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal(EventKind.Decision, all[0].Kind);
        Assert.Equal("Suppressed a release", all[0].Summary);
    }

    [Fact]
    public async Task AddAsync_persists_an_operational_event_with_a_source_display_name()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        var entry = await repository.AddAsync(
            EventKind.SourceFailed,
            summary: "Source failed to respond",
            reason: "Timed out after 10s",
            sourceDisplayName: "Primary NZBHydra",
            detail: null,
            CancellationToken.None);

        Assert.Equal("Primary NZBHydra", entry.SourceDisplayName);
    }

    [Fact]
    public async Task AddAsync_rejects_an_empty_summary_and_persists_nothing()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        await Assert.ThrowsAsync<EventValidationException>(() => repository.AddAsync(
            EventKind.WorkerCycle,
            summary: "   ",
            reason: null,
            sourceDisplayName: null,
            detail: null,
            CancellationToken.None));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("source:1:api_key")]
    [InlineData("sk-abcdef-my-token")]
    [InlineData("Bearer abc123")]
    public async Task AddAsync_rejects_a_source_display_name_that_looks_like_a_credential(string sourceDisplayName)
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        await Assert.ThrowsAsync<EventValidationException>(() => repository.AddAsync(
            EventKind.SourceFailed,
            summary: "Source failed",
            reason: null,
            sourceDisplayName: sourceDisplayName,
            detail: null,
            CancellationToken.None));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PruneAsync_removes_operational_events_past_seven_days_but_keeps_decisions()
    {
        using var context = CreateContext();

        var now = DateTimeOffset.Parse("2026-09-06T12:00:00Z");
        var timeProvider = new FakeTimeProvider(now);
        var repository = new EventRepository(context, timeProvider);

        // A decision older than the 7-day operational window but well inside the 180-day decision
        // window — must survive pruning; this is the asymmetry the retention policy exists for.
        context.Events.Add(new EventEntry
        {
            Kind = EventKind.Decision,
            OccurredAt = now - TimeSpan.FromDays(30),
            Summary = "Old decision, still within decision retention",
        });

        // An operational event just past its 7-day window — must be pruned.
        context.Events.Add(new EventEntry
        {
            Kind = EventKind.WorkerCycle,
            OccurredAt = now - TimeSpan.FromDays(8),
            Summary = "Worker cycle, past operational retention",
        });

        // An operational event well inside its 7-day window — must survive.
        context.Events.Add(new EventEntry
        {
            Kind = EventKind.WorkerCycle,
            OccurredAt = now - TimeSpan.FromDays(1),
            Summary = "Worker cycle, within operational retention",
        });

        // A decision past its 180-day window — must be pruned.
        context.Events.Add(new EventEntry
        {
            Kind = EventKind.Decision,
            OccurredAt = now - TimeSpan.FromDays(181),
            Summary = "Ancient decision, past decision retention",
        });

        await context.SaveChangesAsync();

        var prunedByKind = await repository.PruneAsync(CancellationToken.None);

        Assert.Equal(1, prunedByKind[EventKind.Decision]);
        Assert.Equal(1, prunedByKind[EventKind.WorkerCycle]);

        var remaining = await repository.GetAllAsync(CancellationToken.None);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, e => e.Summary == "Old decision, still within decision retention");
        Assert.Contains(remaining, e => e.Summary == "Worker cycle, within operational retention");
    }

    [Fact]
    public async Task PruneAsync_is_a_noop_when_nothing_is_past_its_retention_window()
    {
        using var context = CreateContext();
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-06T12:00:00Z"));
        var repository = new EventRepository(context, timeProvider);

        await repository.AddAsync(
            EventKind.Decision, "Fresh decision", null, null, null, CancellationToken.None);
        await repository.AddAsync(
            EventKind.WorkerCycle, "Fresh cycle", null, null, null, CancellationToken.None);

        var prunedByKind = await repository.PruneAsync(CancellationToken.None);

        Assert.Empty(prunedByKind);
        Assert.Equal(2, (await repository.GetAllAsync(CancellationToken.None)).Count);
    }

    // ---- QueryAsync: the #55 step 3 read surface --------------------------------------------
    //
    // These exercise the paged/filtered read that the Activity surface and GET /api/activity sit
    // on. Nothing here writes a new kind, column or table: the store under test is the one PR #70
    // shipped, read through a different method.

    /// <summary>
    /// Seeds <paramref name="count"/> events of one kind, one hour apart ascending, so the newest
    /// row is also the highest Id — the ordering both the surface and the cursor rely on.
    /// </summary>
    private static async Task SeedAsync(
        EventRepository repository,
        FakeTimeProvider clock,
        int count,
        EventKind kind = EventKind.WorkerCycle,
        string prefix = "event")
    {
        for (var i = 0; i < count; i++)
        {
            await repository.AddAsync(kind, $"{prefix} {i}", null, null, null, CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
        }
    }

    [Fact]
    public async Task QueryAsync_returns_the_newest_events_first()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, 3);

        var page = await repository.QueryAsync(new EventQuery(), CancellationToken.None);

        Assert.Equal(new[] { "event 2", "event 1", "event 0" }, page.Events.Select(e => e.Summary));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task QueryAsync_pages_through_every_event_exactly_once()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, 10);

        var seen = new List<string>();
        long? cursor = null;
        do
        {
            var page = await repository.QueryAsync(
                new EventQuery(Cursor: cursor, Limit: 3), CancellationToken.None);
            seen.AddRange(page.Events.Select(e => e.Summary));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(10, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    /// <summary>
    /// AC8, and the defect the plan (§5) says this kind of surface always ships with: "a new event
    /// arriving mid-page must not cause a row to be skipped or repeated".
    ///
    /// This is the whole reason <see cref="EventQuery.Cursor"/> is a seek cursor rather than an
    /// offset. It is written to FAIL against a skip/take implementation: five events are inserted
    /// between fetching page 1 and page 2, which under OFFSET paging shifts the window five rows
    /// back and re-serves rows already returned. Under a cursor anchored to the last Id actually
    /// delivered, what arrives above the boundary cannot affect what comes below it.
    /// </summary>
    [Fact]
    public async Task QueryAsync_does_not_skip_or_repeat_rows_when_events_arrive_mid_page()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, 10, prefix: "original");

        var first = await repository.QueryAsync(new EventQuery(Limit: 3), CancellationToken.None);
        Assert.Equal(new[] { "original 9", "original 8", "original 7" }, first.Events.Select(e => e.Summary));

        // Five new events land while the reader sits between pages — the concurrent insertion.
        await SeedAsync(repository, clock, 5, prefix: "arrived-later");

        var second = await repository.QueryAsync(
            new EventQuery(Cursor: first.NextCursor, Limit: 3), CancellationToken.None);

        // Continues exactly where page 1 stopped: no row repeated, none jumped over. Under offset
        // paging this would have re-served "original 8"/"original 7" and eventually skipped rows.
        Assert.Equal(new[] { "original 6", "original 5", "original 4" }, second.Events.Select(e => e.Summary));

        // And the newly-arrived events are not injected into a page below their position; they
        // belong above the cursor, where a reader starting fresh will see them.
        Assert.DoesNotContain(second.Events, e => e.Summary.StartsWith("arrived-later", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueryAsync_filters_by_kind()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, 3, EventKind.Decision, "decision");
        await SeedAsync(repository, clock, 2, EventKind.SourceFailed, "failure");

        var page = await repository.QueryAsync(
            new EventQuery(Kind: EventKind.Decision), CancellationToken.None);

        Assert.Equal(3, page.Events.Count);
        Assert.All(page.Events, e => Assert.Equal(EventKind.Decision, e.Kind));
    }

    /// <summary>The kind and time filters must COMPOSE, not override one another (plan §5).</summary>
    [Fact]
    public async Task QueryAsync_composes_the_kind_and_time_filters()
    {
        using var context = CreateContext();
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var clock = new FakeTimeProvider(start);
        var repository = new EventRepository(context, clock);

        // Interleaved so that neither filter alone can produce the expected answer: 6 events one
        // hour apart, alternating Decision / WorkerCycle.
        for (var i = 0; i < 6; i++)
        {
            await repository.AddAsync(
                i % 2 == 0 ? EventKind.Decision : EventKind.WorkerCycle,
                $"event {i}",
                null,
                null,
                null,
                CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
        }

        // Decisions are events 0, 2, 4 (at +0h, +2h, +4h). The window [+2h, +4h) admits only one of
        // them, so a filter that ignored either half would return 2 or 3 rows instead of 1.
        var page = await repository.QueryAsync(
            new EventQuery(
                Kind: EventKind.Decision,
                Since: start + TimeSpan.FromHours(2),
                Until: start + TimeSpan.FromHours(4)),
            CancellationToken.None);

        Assert.Equal("event 2", Assert.Single(page.Events).Summary);
    }

    [Fact]
    public async Task QueryAsync_clamps_an_oversized_limit_rather_than_serving_the_whole_table()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, 5);

        // int.MaxValue must not become an unbounded read on an un-gated endpoint.
        var page = await repository.QueryAsync(new EventQuery(Limit: int.MaxValue), CancellationToken.None);

        Assert.Equal(5, page.Events.Count);
    }

    [Fact]
    public async Task QueryAsync_returns_an_empty_page_and_no_cursor_when_the_store_is_empty()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        var page = await repository.QueryAsync(new EventQuery(), CancellationToken.None);

        Assert.Empty(page.Events);
        Assert.Null(page.NextCursor);
    }

    // ---- CountAsync: the aggregate QueryAsync cannot express (#77 item 1) ----------------------
    //
    // EVERY TEST BELOW SEEDS MORE THAN EventQuery.MaxLimit (200) ROWS ON PURPOSE, and that is the
    // whole point of the fixture size rather than an arbitrary large number. The defect this method
    // exists to prevent is a count that silently reports a PAGE's worth: an implementation that
    // paged, clamped, or honoured EventQuery.Limit would return 200 (or 50, the default) here and
    // look perfectly healthy. A table below the limit could not tell the two apart, so the
    // assertions would be vacuous against precisely the implementation most likely to be written.
    //
    // The filter cases mirror the QueryAsync tests above one for one (kind, kind+window composed,
    // shadow mode) because the contract is that the two agree about which rows are in scope; a
    // divergence would make "47 of 52" disagree with the list the operator is looking at.

    /// <summary>
    /// Rows above <see cref="EventQuery.MaxLimit"/>, so a paged or clamped count is distinguishable
    /// from a real one. 260 is the smallest round figure comfortably above both the 200 limit and
    /// <c>MinimumScanBatch</c> (256), so a batched implementation must take more than one trip too.
    /// </summary>
    private const int AboveMaxLimit = 260;

    [Fact]
    public async Task CountAsync_counts_the_whole_window_not_one_page()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, AboveMaxLimit);

        var count = await repository.CountAsync(new EventQuery(), CancellationToken.None);

        // A count that honoured Limit would be 50 (the default) and one that clamped to the ceiling
        // would be 200. Neither is the answer to "how many events are there".
        Assert.Equal(AboveMaxLimit, count);
    }

    [Fact]
    public async Task CountAsync_ignores_the_limit_and_cursor_the_query_carries()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, AboveMaxLimit);

        // Take a real cursor from a real page, so this is the shape a caller actually holds rather
        // than an invented id.
        var page = await repository.QueryAsync(new EventQuery(Limit: 10), CancellationToken.None);
        Assert.NotNull(page.NextCursor);

        var count = await repository.CountAsync(
            new EventQuery(Cursor: page.NextCursor, Limit: 10), CancellationToken.None);

        // The count is about the WINDOW, not about what this reader has left to fetch: honouring
        // the cursor would answer AboveMaxLimit - 10 and honouring the limit would answer 10.
        Assert.Equal(AboveMaxLimit, count);
    }

    [Fact]
    public async Task CountAsync_filters_by_kind()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, AboveMaxLimit, EventKind.Decision, "decision");
        await SeedAsync(repository, clock, 7, EventKind.SourceFailed, "failure");

        Assert.Equal(
            AboveMaxLimit,
            await repository.CountAsync(new EventQuery(Kind: EventKind.Decision), CancellationToken.None));
        Assert.Equal(
            7,
            await repository.CountAsync(new EventQuery(Kind: EventKind.SourceFailed), CancellationToken.None));
        Assert.Equal(
            AboveMaxLimit + 7,
            await repository.CountAsync(new EventQuery(), CancellationToken.None));
    }

    /// <summary>
    /// The kind and time filters must COMPOSE here exactly as they do in
    /// <see cref="QueryAsync_composes_the_kind_and_time_filters"/> — same half-open window, same
    /// interleaving, just counted instead of listed.
    /// </summary>
    [Fact]
    public async Task CountAsync_composes_the_kind_and_time_filters_over_a_table_larger_than_one_page()
    {
        using var context = CreateContext();
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var clock = new FakeTimeProvider(start);
        var repository = new EventRepository(context, clock);

        // 300 events one hour apart, alternating Decision / WorkerCycle, so neither filter alone
        // produces the expected answer and the table is comfortably past MaxLimit.
        const int total = 300;
        for (var i = 0; i < total; i++)
        {
            await repository.AddAsync(
                i % 2 == 0 ? EventKind.Decision : EventKind.WorkerCycle,
                $"event {i}",
                null,
                null,
                null,
                CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
        }

        // Decisions sit at the even hours. The window [+10h, +210h) therefore admits the decisions
        // at +10h, +12h, ... +208h -- 100 of them. Both bounds bite (there are 150 decisions in
        // total) and the answer is well past a page, so a clamped count would report 200.
        var count = await repository.CountAsync(
            new EventQuery(
                Kind: EventKind.Decision,
                Since: start + TimeSpan.FromHours(10),
                Until: start + TimeSpan.FromHours(210)),
            CancellationToken.None);

        Assert.Equal(100, count);

        // Dropping the kind filter must double it -- proof the two filters compose rather than one
        // overriding the other.
        var bothKinds = await repository.CountAsync(
            new EventQuery(
                Since: start + TimeSpan.FromHours(10),
                Until: start + TimeSpan.FromHours(210)),
            CancellationToken.None);

        Assert.Equal(200, bothKinds);
    }

    /// <summary>
    /// The window is half-open exactly as <see cref="EventRepository.QueryAsync"/> treats it: Since
    /// is inclusive, Until is exclusive. Asserted on the boundary rows themselves, because an
    /// off-by-one here would silently move one event between "counted" and "not counted" without
    /// any test over a wider window noticing.
    /// </summary>
    [Fact]
    public async Task CountAsync_treats_the_window_as_half_open_the_same_way_QueryAsync_does()
    {
        using var context = CreateContext();
        var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var clock = new FakeTimeProvider(start);
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, AboveMaxLimit);

        // Events sit at start + 0h, 1h, ... The window [start+1h, start+4h) admits 1h, 2h and 3h.
        var query = new EventQuery(
            Since: start + TimeSpan.FromHours(1),
            Until: start + TimeSpan.FromHours(4));

        var count = await repository.CountAsync(query, CancellationToken.None);
        Assert.Equal(3, count);

        // And it agrees with the listing read over the identical filters -- the contract that keeps
        // an aggregate from disagreeing with the page an operator is looking at.
        var page = await repository.QueryAsync(query with { Limit = EventQuery.MaxLimit }, CancellationToken.None);
        Assert.Equal(page.Events.Count, count);
    }

    [Fact]
    public async Task CountAsync_filters_by_shadow_mode_and_excludes_rows_with_no_answer()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);

        for (var i = 0; i < AboveMaxLimit; i++)
        {
            await repository.AddAsync(
                EventKind.Decision, $"decision {i}", null, null, null, CancellationToken.None,
                shadowMode: i % 2 == 0);
            clock.Advance(TimeSpan.FromHours(1));
        }

        // Operational rows carry no shadow-mode answer at all (the column is null).
        await SeedAsync(repository, clock, 5, EventKind.WorkerCycle, "cycle");

        var shadow = await repository.CountAsync(new EventQuery(ShadowMode: true), CancellationToken.None);
        var live = await repository.CountAsync(new EventQuery(ShadowMode: false), CancellationToken.None);

        Assert.Equal(AboveMaxLimit / 2, shadow);
        Assert.Equal(AboveMaxLimit / 2, live);

        // The five WorkerCycle rows match NEITHER branch -- a row with no shadow-mode answer is not
        // a shadow decision and not a live one, the same three-valued behaviour QueryAsync relies on.
        Assert.Equal(AboveMaxLimit + 5, await repository.CountAsync(new EventQuery(), CancellationToken.None));
    }

    [Fact]
    public async Task CountAsync_returns_zero_when_nothing_matches()
    {
        using var context = CreateContext();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var repository = new EventRepository(context, clock);
        await SeedAsync(repository, clock, 3, EventKind.WorkerCycle);

        Assert.Equal(
            0,
            await repository.CountAsync(new EventQuery(Kind: EventKind.Decision), CancellationToken.None));
        Assert.Equal(
            0,
            await repository.CountAsync(
                new EventQuery(Since: DateTimeOffset.Parse("2030-01-01T00:00:00Z")),
                CancellationToken.None));
    }

    // ---- #54: the review verdict on Decision rows, and the agreement aggregate ----

    /// <summary>Adds one decision carrying the shadow-mode flag it was made under.</summary>
    private static Task<EventEntry> AddDecisionAsync(EventRepository repository, bool shadowMode) =>
        repository.AddAsync(
            EventKind.Decision,
            shadowMode ? "Release flagged in shadow mode (still served)" : "Release suppressed",
            reason: "a test decision",
            sourceDisplayName: null,
            detail: null,
            CancellationToken.None,
            shadowMode: shadowMode);

    /// <summary>
    /// #54 AC1 / plan section 5: the flag is stored AS OF DECISION TIME, so a decision made under
    /// shadow mode still reads as one after the switch is flipped. Nothing in this test flips a
    /// setting - that is the point: the row carries its own answer, so there is no later state that
    /// COULD change it. A design that inferred shadow mode from the current setting would have no
    /// way to pass this without a second source of truth.
    /// </summary>
    [Fact]
    public async Task Decisions_record_the_shadow_mode_flag_as_of_decision_time()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        await AddDecisionAsync(repository, shadowMode: true);
        await AddDecisionAsync(repository, shadowMode: false);

        var shadow = await repository.QueryAsync(
            new EventQuery(Kind: EventKind.Decision, ShadowMode: true), CancellationToken.None);
        var live = await repository.QueryAsync(
            new EventQuery(Kind: EventKind.Decision, ShadowMode: false), CancellationToken.None);

        Assert.All(shadow.Events, e => Assert.True(e.ShadowMode));
        Assert.All(live.Events, e => Assert.False(e.ShadowMode));
        Assert.Single(shadow.Events);
        Assert.Single(live.Events);
    }

    /// <summary>
    /// Operational events carry no shadow-mode answer, so they match NEITHER filter branch rather
    /// than defaulting into the "live" one - which would quietly pad the review queue with rows
    /// that were never decisions.
    /// </summary>
    [Fact]
    public async Task Non_decision_events_match_neither_shadow_mode_filter_branch()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        await repository.AddAsync(
            EventKind.WorkerCycle, "cycle ran", null, null, null, CancellationToken.None);

        var shadow = await repository.QueryAsync(new EventQuery(ShadowMode: true), CancellationToken.None);
        var live = await repository.QueryAsync(new EventQuery(ShadowMode: false), CancellationToken.None);

        Assert.Empty(shadow.Events);
        Assert.Empty(live.Events);
    }

    /// <summary>
    /// Plan section 5: reviewing twice UPDATES, never duplicates. The verdict is a column on the
    /// decision's own row, so a second review overwrites the first - a verdict table would have
    /// appended, and the agreement rate would then count one revisited decision twice.
    /// </summary>
    [Fact]
    public async Task ReviewAsync_is_idempotent_per_decision()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);
        var decision = await AddDecisionAsync(repository, shadowMode: true);

        await repository.ReviewAsync(decision.Id, ReviewVerdict.Agree, "first", CancellationToken.None);
        await repository.ReviewAsync(decision.Id, ReviewVerdict.Disagree, "second", CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        var row = Assert.Single(all);
        Assert.Equal(ReviewVerdict.Disagree, row.ReviewVerdict);
        Assert.Equal("second", row.ReviewNote);

        // One decision reviewed once, however many times the operator changed their mind.
        var agreement = await repository.GetAgreementAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.Equal(1, agreement.Reviewed);
    }

    [Fact]
    public async Task ReviewAsync_returns_null_for_a_non_decision_row()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        // A verdict on "the worker ran a cycle" is meaningless, and admitting one would put a
        // non-decision into the agreement rate's denominator.
        var operational = await repository.AddAsync(
            EventKind.WorkerCycle, "cycle ran", null, null, null, CancellationToken.None);

        var reviewed = await repository.ReviewAsync(
            operational.Id, ReviewVerdict.Agree, null, CancellationToken.None);

        Assert.Null(reviewed);
    }

    [Fact]
    public async Task ReviewAsync_rejects_a_note_longer_than_the_column_allows()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);
        var decision = await AddDecisionAsync(repository, shadowMode: true);

        var tooLong = new string('x', EventRepository.ReviewNoteMaxLength + 1);

        // Rejected, not truncated (AC24): storing a shortened version of the operator's own words
        // would leave them believing they recorded something they did not.
        await Assert.ThrowsAsync<EventValidationException>(
            () => repository.ReviewAsync(decision.Id, ReviewVerdict.Agree, tooLong, CancellationToken.None));
    }

    /// <summary>
    /// Plan section 5: the aggregate's arithmetic against a known fixture set - four agreed, one
    /// disagreed, and one left unreviewed, which must count toward NEITHER.
    /// </summary>
    [Fact]
    public async Task GetAgreementAsync_counts_only_reviewed_decisions()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        for (var i = 0; i < 4; i++)
        {
            var agreed = await AddDecisionAsync(repository, shadowMode: true);
            await repository.ReviewAsync(agreed.Id, ReviewVerdict.Agree, null, CancellationToken.None);
        }

        var disagreed = await AddDecisionAsync(repository, shadowMode: true);
        await repository.ReviewAsync(disagreed.Id, ReviewVerdict.Disagree, null, CancellationToken.None);

        // Unreviewed: present in the store, absent from both counts.
        await AddDecisionAsync(repository, shadowMode: true);

        var agreement = await repository.GetAgreementAsync(DateTimeOffset.MinValue, CancellationToken.None);

        Assert.Equal(4, agreement.Agreed);
        Assert.Equal(1, agreement.Disagreed);
        Assert.Equal(5, agreement.Reviewed);
    }

    /// <summary>
    /// AC4's zero-reviews case at the arithmetic's source. This must not divide by zero, and it must
    /// report zero REVIEWED rather than zero AGREED-of-some-total - the two are different sentences,
    /// and only the first lets the UI render "no data yet" instead of a measured 0%.
    /// </summary>
    [Fact]
    public async Task GetAgreementAsync_reports_nothing_reviewed_rather_than_dividing_by_zero()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        await AddDecisionAsync(repository, shadowMode: true);

        var agreement = await repository.GetAgreementAsync(DateTimeOffset.MinValue, CancellationToken.None);

        Assert.Equal(0, agreement.Agreed);
        Assert.Equal(0, agreement.Disagreed);
        Assert.Equal(0, agreement.Reviewed);
    }

    /// <summary>The window is a real filter: a review older than it is not counted.</summary>
    [Fact]
    public async Task GetAgreementAsync_excludes_decisions_older_than_the_window()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        using var context = CreateContext();
        var repository = new EventRepository(context, clock);

        var old = await AddDecisionAsync(repository, shadowMode: true);
        await repository.ReviewAsync(old.Id, ReviewVerdict.Agree, null, CancellationToken.None);

        clock.Advance(TimeSpan.FromDays(30));

        var recent = await AddDecisionAsync(repository, shadowMode: true);
        await repository.ReviewAsync(recent.Id, ReviewVerdict.Agree, null, CancellationToken.None);

        var lastWeek = await repository.GetAgreementAsync(
            clock.GetUtcNow() - TimeSpan.FromDays(7), CancellationToken.None);

        Assert.Equal(1, lastWeek.Reviewed);
    }

    /// <summary>
    /// #54 AC1 through the BATCH path, which is the half that had no coverage and was therefore
    /// wrong: AddRangeAsync's tuple omitted ShadowMode entirely, so a Decision written in a burst
    /// persisted a NULL flag.
    ///
    /// A NULL flag is not a harmless default here. The shadow-mode filter compares the nullable
    /// column as a value, so SQL's three-valued logic excludes NULL rows from BOTH branches -- such
    /// a row would be invisible under "Shadow-only" AND under "Enforced" while rendering as
    /// "Unknown". FilterStage emits one Decision per suppressed release and is exactly the caller
    /// that would batch, so this was a live defect waiting on its first batching caller rather than
    /// a theoretical one.
    ///
    /// Asserted per row rather than in aggregate: a test that only counted rows would pass against
    /// an implementation that wrote one flag to every row in the batch.
    /// </summary>
    [Fact]
    public async Task Decisions_written_in_a_batch_each_keep_their_own_shadow_mode_flag()
    {
        using var context = CreateContext();
        var repository = new EventRepository(context);

        await repository.AddRangeAsync(
            new (EventKind, string, string?, string?, string?, bool?)[]
            {
                (EventKind.Decision, "Flagged in shadow mode", "shadow", null, null, true),
                (EventKind.Decision, "Suppressed for real", "enforced", null, null, false),
                (EventKind.WorkerCycle, "A cycle ran", null, null, null, null),
            },
            CancellationToken.None);

        var rows = await repository.GetAllAsync(CancellationToken.None);

        Assert.True(rows.Single(r => r.Summary == "Flagged in shadow mode").ShadowMode);
        Assert.False(rows.Single(r => r.Summary == "Suppressed for real").ShadowMode);

        // The operational kind still has no answer to give, and must not be coerced into one.
        Assert.Null(rows.Single(r => r.Summary == "A cycle ran").ShadowMode);
    }
}
