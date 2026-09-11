using Arbitarr.Core.Releases;
using Arbitarr.Data.Search;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-tps: proves the durable release lookup against a REAL SQLite database rather than against
/// C# properties — every assertion here reads back a row that was written and re-read through EF,
/// because the defect being fixed is precisely that nothing survived the process.
/// </summary>
public sealed class ReleaseLookupStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(14);

    private readonly SqliteTestDatabase _database = new("arr-searcher-release-lookup-store-test");
    private readonly FakeTimeProvider _timeProvider = new(Now);

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// arb-zwk: the store now takes a TTL READER rather than a TimeSpan, so the value is awaited at
    /// the point of use instead of blocking a thread at construction. Every caller here keeps passing
    /// a plain TimeSpan and this wraps it, so the existing tests still pin the same behaviour.
    /// </summary>
    private ReleaseLookupStore CreateStore(ArbitarrDbContext context, TimeSpan? ttl = null) =>
        new(context, _ => Task.FromResult(ttl ?? Ttl), _timeProvider);

    private static ReleaseCandidate Candidate(string guid, string link = "https://indexer.example.invalid/get/abc") => new()
    {
        Title = "Some.Release.S01E01.1080p",
        Guid = guid,
        PubDate = Now - TimeSpan.FromHours(3),
        Size = 1_234_567_890,
        Link = new Uri(link),
        Category = new[] { 5040 },
        Protocol = ProtocolKind.Usenet,
        UsenetGroup = new[] { "alt.binaries.example" },
    };

    /// <summary>
    /// The headline round trip: what goes in comes back out THROUGH the database, across two
    /// separate contexts, so nothing is being served from a tracked in-memory entity. The candidate
    /// fields asserted are the ones the download path actually consumes — the link above all, since
    /// the source re-validates its origin at fetch time.
    /// </summary>
    [Fact]
    public async Task A_recorded_release_is_resolved_back_from_a_separate_context()
    {
        var candidate = Candidate("upstream-guid-1");

        using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
            await CreateStore(context).UpsertRangeAsync(
                new[] { new StoredRelease("proxy-1", "hydra", candidate) });
        }

        using (var context = CreateContext())
        {
            var found = await CreateStore(context).FindAsync("proxy-1");

            Assert.NotNull(found);
            Assert.Equal("proxy-1", found.ProxyGuid);
            Assert.Equal("hydra", found.SourceName);
            Assert.Equal(candidate.Guid, found.Candidate.Guid);
            Assert.Equal(candidate.Title, found.Candidate.Title);
            Assert.Equal(candidate.Link, found.Candidate.Link);
            Assert.Equal(candidate.Size, found.Candidate.Size);
            Assert.Equal(candidate.Protocol, found.Candidate.Protocol);
            Assert.Equal(candidate.Category, found.Candidate.Category);
        }
    }

    /// <summary>
    /// An expired row answers null on READ, without waiting for the maintenance prune. This is the
    /// property that makes it safe to prune on a timer (see MaintenanceJob.PruneReleaseLookupAsync):
    /// if the read trusted the prune, a job that ran late would keep a dead link alive.
    ///
    /// <para>The row is asserted to still be PRESENT afterwards, which is what distinguishes "the
    /// read evaluated expiry" from "the row happened not to be there".</para>
    /// </summary>
    [Fact]
    public async Task An_expired_row_resolves_to_null_while_still_present_in_the_table()
    {
        using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
            await CreateStore(context, TimeSpan.FromHours(1)).UpsertRangeAsync(
                new[] { new StoredRelease("proxy-expiring", "hydra", Candidate("upstream-guid-2")) });
        }

        // Establish it resolved BEFORE the clock moved, so the null below is attributable to the
        // expiry rather than to the row never having been readable at all.
        using (var context = CreateContext())
        {
            Assert.NotNull(await CreateStore(context).FindAsync("proxy-expiring"));
        }

        _timeProvider.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        using (var context = CreateContext())
        {
            Assert.Null(await CreateStore(context).FindAsync("proxy-expiring"));
            Assert.True(await context.ReleaseLookupEntries.AnyAsync(e => e.ProxyGuid == "proxy-expiring"));
        }
    }

    /// <summary>
    /// Re-recording the same proxy guid UPDATES the row rather than inserting a second one — the
    /// unique index would otherwise turn the second search returning the same release into an
    /// exception on the search path. Asserted by row COUNT, not merely by a successful read: a
    /// duplicate would still let a read succeed right up until SingleOrDefault threw.
    /// </summary>
    [Fact]
    public async Task Re_recording_the_same_guid_updates_the_row_rather_than_duplicating_it()
    {
        using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
            await CreateStore(context).UpsertRangeAsync(
                new[] { new StoredRelease("proxy-dup", "hydra", Candidate("upstream-guid-3", "https://indexer.example.invalid/get/first")) });
        }

        _timeProvider.Advance(TimeSpan.FromHours(2));

        using (var context = CreateContext())
        {
            await CreateStore(context).UpsertRangeAsync(
                new[] { new StoredRelease("proxy-dup", "hydra", Candidate("upstream-guid-3", "https://indexer.example.invalid/get/second")) });
        }

        using (var context = CreateContext())
        {
            Assert.Equal(1, await context.ReleaseLookupEntries.CountAsync(e => e.ProxyGuid == "proxy-dup"));

            var found = await CreateStore(context).FindAsync("proxy-dup");
            Assert.NotNull(found);
            Assert.Equal(new Uri("https://indexer.example.invalid/get/second"), found.Candidate.Link);

            // The expiry was extended by the re-record, not left at the original write's value: a
            // release still being returned by searches is one an *arr may still grab.
            var row = await context.ReleaseLookupEntries.SingleAsync(e => e.ProxyGuid == "proxy-dup");
            Assert.Equal(Now + TimeSpan.FromHours(2), row.RecordedAt);
            Assert.Equal(Now + TimeSpan.FromHours(2) + Ttl, row.ExpiresAt);
        }
    }

    /// <summary>An unknown guid is a miss, not an exception — the download proxy answers it as 404.</summary>
    [Fact]
    public async Task An_unknown_guid_resolves_to_null()
    {
        using var context = CreateContext();
        await context.Database.MigrateAsync();

        Assert.Null(await CreateStore(context).FindAsync("never-recorded"));
    }

    /// <summary>
    /// A batch containing several releases writes them all in one pass, which is how the search
    /// path uses this — one call per search carrying the whole post-filter page.
    /// </summary>
    [Fact]
    public async Task A_batch_records_every_release_it_is_given()
    {
        var batch = Enumerable.Range(0, 25)
            .Select(i => new StoredRelease($"proxy-batch-{i}", "hydra", Candidate($"upstream-batch-{i}")))
            .ToList();

        using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
            await CreateStore(context).UpsertRangeAsync(batch);
        }

        using (var context = CreateContext())
        {
            var store = CreateStore(context);
            foreach (var release in batch)
            {
                Assert.NotNull(await store.FindAsync(release.ProxyGuid));
            }
        }
    }
}
