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
}
