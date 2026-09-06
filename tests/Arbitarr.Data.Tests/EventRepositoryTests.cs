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
}
