using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Diagnostics;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-v3w: proves the durable download-refusal store against a REAL SQLite database rather than
/// against C# properties — every assertion reads a row back through a SEPARATE context, because the
/// defect being fixed is precisely that nothing survived the process.
/// </summary>
public sealed class DownloadRefusalStoreTests : IDisposable
{
    private static readonly DateTimeOffset First = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new("arr-searcher-download-refusal-store-test");

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

    [Fact]
    public async Task A_recorded_refusal_is_read_back_from_a_separate_context()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            await new DownloadRefusalStore(context).UpsertAsync(
                new DownloadRefusal("nzbhydra2", "Refused HTTP 302: the source redirected instead of serving the file.", First, First));
        }

        using (var context = CreateContext())
        {
            var refusal = Assert.Single(await new DownloadRefusalStore(context).LoadAllAsync());
            Assert.Equal("nzbhydra2", refusal.SourceName);
            Assert.Contains("302", refusal.Reason);
            Assert.Equal(First, refusal.ObservedSinceUtc);
            Assert.Equal(First, refusal.LastObservedUtc);
        }
    }

    /// <summary>
    /// THE HEADLINE RULE. A repeat upsert must refresh the existing row rather than reset the
    /// observation window — an hours-old misconfiguration reading as brand new on every Sonarr retry
    /// is exactly the symptom arb-ln0 exists to prevent, and persisting it would be pointless if the
    /// durable copy lost that instant. ObservedSinceUtc is supplied by the tracker (which owns the
    /// preserve rule), so what this pins is that the store WRITES WHAT IT IS GIVEN and does not
    /// substitute a fresh instant of its own.
    /// </summary>
    [Fact]
    public async Task A_repeat_upsert_preserves_the_observed_since_instant_and_advances_the_last_observed_one()
    {
        var later = First.AddMinutes(30);

        using (var context = await CreateMigratedContextAsync())
        {
            var store = new DownloadRefusalStore(context);
            await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "first", First, First));
            await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "second", First, later));
        }

        using (var context = CreateContext())
        {
            var refusal = Assert.Single(await new DownloadRefusalStore(context).LoadAllAsync());
            Assert.Equal(First, refusal.ObservedSinceUtc);
            Assert.Equal(later, refusal.LastObservedUtc);
            Assert.Equal("second", refusal.Reason);
        }
    }

    /// <summary>
    /// The other half of the rule above: one row per source, never two. The unique index is what
    /// bounds this table (and is why it needs no prune), so a repeat producing a second row would be
    /// both a wrong answer and an unbounded-growth defect.
    /// </summary>
    [Fact]
    public async Task A_repeat_upsert_leaves_exactly_one_row_for_the_source()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            var store = new DownloadRefusalStore(context);
            await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "first", First, First));
            await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "second", First, First.AddMinutes(5)));
            await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "third", First, First.AddMinutes(10)));
        }

        using (var context = CreateContext())
        {
            // Counted against the TABLE, not against the store's projection, so a store that
            // silently de-duplicated on read could not hide a duplicate row from this assertion.
            Assert.Equal(1, await context.DownloadRefusalEntries.CountAsync(e => e.SourceName == "nzbhydra2"));
        }
    }

    [Fact]
    public async Task A_successful_grab_deletes_the_row()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            await new DownloadRefusalStore(context).UpsertAsync(
                new DownloadRefusal("nzbhydra2", "refused", First, First));
        }

        // Positive control: the row demonstrably EXISTS before the delete, so the emptiness asserted
        // afterwards is the delete working rather than the write never having landed.
        using (var context = CreateContext())
        {
            Assert.Single(await new DownloadRefusalStore(context).LoadAllAsync());
        }

        using (var context = CreateContext())
        {
            await new DownloadRefusalStore(context).DeleteAsync("nzbhydra2");
        }

        using (var context = CreateContext())
        {
            Assert.Empty(await new DownloadRefusalStore(context).LoadAllAsync());
        }
    }

    [Fact]
    public async Task Deleting_one_source_leaves_another_sources_refusal_in_place()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            var store = new DownloadRefusalStore(context);
            await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "refused", First, First));
            await store.UpsertAsync(new DownloadRefusal("other-source", "refused", First, First));
        }

        using (var context = CreateContext())
        {
            await new DownloadRefusalStore(context).DeleteAsync("nzbhydra2");
        }

        using (var context = CreateContext())
        {
            // A working second source proves nothing about the first, and vice versa: the table is
            // per source, so a delete must not be a clear-all.
            var refusal = Assert.Single(await new DownloadRefusalStore(context).LoadAllAsync());
            Assert.Equal("other-source", refusal.SourceName);
        }
    }

    [Fact]
    public async Task Deleting_a_source_that_never_refused_is_a_no_op()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            await new DownloadRefusalStore(context).UpsertAsync(
                new DownloadRefusal("nzbhydra2", "refused", First, First));
        }

        using (var context = CreateContext())
        {
            await new DownloadRefusalStore(context).DeleteAsync("never-seen");
        }

        using (var context = CreateContext())
        {
            Assert.Single(await new DownloadRefusalStore(context).LoadAllAsync());
        }
    }

    [Fact]
    public async Task Refusals_are_loaded_ordered_by_source_name()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            var store = new DownloadRefusalStore(context);
            await store.UpsertAsync(new DownloadRefusal("zeta", "refused", First, First));
            await store.UpsertAsync(new DownloadRefusal("alpha", "refused", First, First));
        }

        using (var context = CreateContext())
        {
            var refusals = await new DownloadRefusalStore(context).LoadAllAsync();
            Assert.Equal(new[] { "alpha", "zeta" }, refusals.Select(r => r.SourceName).ToArray());
        }
    }

    [Fact]
    public async Task A_fresh_database_loads_nothing()
    {
        using var context = await CreateMigratedContextAsync();

        Assert.Empty(await new DownloadRefusalStore(context).LoadAllAsync());
    }
}
