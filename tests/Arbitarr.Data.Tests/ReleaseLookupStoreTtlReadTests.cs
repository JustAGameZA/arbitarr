using Arbitarr.Core.Releases;
using Arbitarr.Data.Search;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-zwk: WHEN the release-lookup TTL is read.
///
/// <para><b>The defect this pins.</b> The store is registered SCOPED, so taking the TTL as a
/// constructor argument made the composition root resolve it on every scope creation — and because
/// the setting lives behind an async reader, that resolution was a
/// <c>GetAwaiter().GetResult()</c> blocking a thread-pool thread on a database round trip for every
/// search, to fetch a value only the write path uses. Handing the store the READER instead moves the
/// read to the point of use, where it can be awaited.</para>
///
/// <para>These tests assert the timing rather than the value, because the value was never wrong —
/// the cost was. A counting reader is the only thing that can tell "read once per write" from "read
/// once per scope"; asserting the resulting <c>ExpiresAt</c> cannot, since both shapes produce the
/// same row.</para>
/// </summary>
public sealed class ReleaseLookupStoreTtlReadTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(14);

    private readonly SqliteTestDatabase _database = new("arr-searcher-release-lookup-ttl-read-test");
    private readonly FakeTimeProvider _timeProvider = new(Now);

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static ReleaseCandidate Candidate(string guid) => new()
    {
        Title = "Some.Release.S01E01.1080p",
        Guid = guid,
        PubDate = Now - TimeSpan.FromHours(3),
        Size = 1_234,
        Link = new Uri("https://indexer.example.invalid/get/abc"),
    };

    /// <summary>
    /// Constructing the store reads nothing. This is the assertion that fails if the TTL is moved
    /// back into the constructor — which is exactly the change that reintroduced the per-scope
    /// blocking read.
    /// </summary>
    [Fact]
    public void Constructing_the_store_does_not_read_the_ttl()
    {
        using var context = CreateContext();
        var reads = 0;

        _ = new ReleaseLookupStore(
            context,
            _ =>
            {
                reads++;
                return Task.FromResult(Ttl);
            },
            _timeProvider);

        Assert.Equal(0, reads);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL for the test above.</b> The reader is wired up and does get called — on
    /// the write, where the value is actually needed. Without this, "constructing reads nothing"
    /// would pass just as happily against a store that never read the TTL at all.
    /// </summary>
    [Fact]
    public async Task The_first_upsert_reads_the_ttl()
    {
        using var context = CreateContext();
        var reads = 0;
        var store = new ReleaseLookupStore(
            context,
            _ =>
            {
                reads++;
                return Task.FromResult(Ttl);
            },
            _timeProvider);

        Assert.Equal(0, reads);

        await store.UpsertRangeAsync(new[] { new StoredRelease("guid-1", "TestSource", Candidate("guid-1")) });

        Assert.Equal(1, reads);
    }

    /// <summary>
    /// An EMPTY batch is a no-op and must not pay for a settings round trip. The read sits after the
    /// early return for that reason, and this pins the ordering — it is the kind of detail a later
    /// "tidy the top of the method" edit silently reverses.
    /// </summary>
    [Fact]
    public async Task An_empty_batch_reads_nothing()
    {
        using var context = CreateContext();
        var reads = 0;
        var store = new ReleaseLookupStore(
            context,
            _ =>
            {
                reads++;
                return Task.FromResult(Ttl);
            },
            _timeProvider);

        await store.UpsertRangeAsync(Array.Empty<StoredRelease>());

        Assert.Equal(0, reads);
    }

    /// <summary>
    /// The READ path never needs the TTL: <c>FindAsync</c> evaluates the row's stored
    /// <c>ExpiresAt</c>, which is what makes an expired row unresolvable even when the prune has not
    /// run. So a lookup must not trigger a settings read either.
    /// </summary>
    [Fact]
    public async Task A_lookup_does_not_read_the_ttl()
    {
        using var context = CreateContext();
        var reads = 0;
        var store = new ReleaseLookupStore(
            context,
            _ =>
            {
                reads++;
                return Task.FromResult(Ttl);
            },
            _timeProvider);

        _ = await store.FindAsync("guid-absent");

        Assert.Equal(0, reads);
    }

    /// <summary>
    /// The freshness the old shape was built for is preserved: a TTL lowered between two writes
    /// takes effect on the second one, with no restart. This is the behaviour the Program.cs comment
    /// claims, asserted rather than assumed — it is the reason the value is read per use at all,
    /// instead of being resolved once at startup.
    /// </summary>
    [Fact]
    public async Task A_ttl_lowered_between_writes_takes_effect_on_the_next_write()
    {
        using var context = CreateContext();
        var ttl = TimeSpan.FromDays(14);
        var store = new ReleaseLookupStore(context, _ => Task.FromResult(ttl), _timeProvider);

        await store.UpsertRangeAsync(new[] { new StoredRelease("guid-1", "TestSource", Candidate("guid-1")) });

        ttl = TimeSpan.FromHours(1);
        await store.UpsertRangeAsync(new[] { new StoredRelease("guid-2", "TestSource", Candidate("guid-2")) });

        var first = await context.ReleaseLookupEntries.AsNoTracking().SingleAsync(e => e.ProxyGuid == "guid-1");
        var second = await context.ReleaseLookupEntries.AsNoTracking().SingleAsync(e => e.ProxyGuid == "guid-2");

        Assert.Equal(Now + TimeSpan.FromDays(14), first.ExpiresAt);
        Assert.Equal(Now + TimeSpan.FromHours(1), second.ExpiresAt);
    }
}
