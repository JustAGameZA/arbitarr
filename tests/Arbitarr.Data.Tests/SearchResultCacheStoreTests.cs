using Arbitarr.Core.Caching;
using Arbitarr.Data.Caching;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Tests;

/// <summary>
/// Proves the EF Core-backed <see cref="SearchResultCacheStore"/> round-trips
/// <see cref="CachedSearchResult"/> against a real SQLite-backed <c>SearchResultCacheEntry</c> row,
/// honors the "SaveAsync never touches LastRequestedAt" contract (M3-8a), and implements the
/// proactive worker's selection predicate (<c>LastRequestedAt &gt; now - activeWindow AND
/// now &gt;= FreshUntil - refreshLead</c>) against genuinely persisted rows.
/// </summary>
public sealed class SearchResultCacheStoreTests : IDisposable
{
    private readonly string _dbPath;
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public SearchResultCacheStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-searchresultcache-test-{Guid.NewGuid():N}.db");
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
    public async Task GetAsync_ReturnsNull_WhenNoRowExists()
    {
        using var context = CreateContext();
        var store = new SearchResultCacheStore(context);

        var result = await store.GetAsync("no-such-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTripsEntry()
    {
        const string key = "series:12345:s01e01";

        using (var context = CreateContext())
        {
            var store = new SearchResultCacheStore(context);
            await store.SaveAsync(key, "payload-v1", Now, Now + TimeSpan.FromMinutes(5), Now + TimeSpan.FromHours(1));
        }

        using (var context = CreateContext())
        {
            var store = new SearchResultCacheStore(context);
            var loaded = await store.GetAsync(key);

            Assert.NotNull(loaded);
            Assert.Equal("payload-v1", loaded!.PayloadJson);
            Assert.Equal(Now, loaded.FetchedAt);
            Assert.Equal(Now + TimeSpan.FromMinutes(5), loaded.FreshUntil);
            Assert.Equal(Now + TimeSpan.FromHours(1), loaded.ServeUntil);
        }
    }

    [Fact]
    public async Task SaveAsync_NeverTouchesLastRequestedAt()
    {
        const string key = "series:12345:s01e01";
        var stampedAt = Now + TimeSpan.FromMinutes(1);

        using var context = CreateContext();
        var store = new SearchResultCacheStore(context);

        await store.SaveAsync(key, "payload-v1", Now, Now + TimeSpan.FromMinutes(5), Now + TimeSpan.FromHours(1));
        await store.TouchLastRequestedAsync(key, stampedAt);

        await store.SaveAsync(key, "payload-v2", Now + TimeSpan.FromMinutes(2), Now + TimeSpan.FromMinutes(7), Now + TimeSpan.FromHours(1));

        var reloaded = await store.GetAsync(key);
        Assert.NotNull(reloaded);
        Assert.Equal("payload-v2", reloaded!.PayloadJson);
        Assert.Equal(stampedAt, reloaded.LastRequestedAt);
    }

    [Fact]
    public async Task TouchLastRequestedAsync_UnknownKey_DoesNotThrow()
    {
        using var context = CreateContext();
        var store = new SearchResultCacheStore(context);

        await store.TouchLastRequestedAsync("no-such-key", Now);
        // No exception, no row created.
        Assert.Null(await store.GetAsync("no-such-key"));
    }

    [Fact]
    public async Task GetRefreshCandidatesAsync_SelectsEntries_MatchingActiveWindowAndRefreshLead()
    {
        using var context = CreateContext();
        var store = new SearchResultCacheStore(context);

        var activeWindow = TimeSpan.FromHours(1);
        var refreshLead = TimeSpan.FromMinutes(5);

        // Eligible: requested recently, and now is within refreshLead of FreshUntil.
        await SeedAsync(store, "eligible", freshUntil: Now + TimeSpan.FromMinutes(3), lastRequestedAt: Now - TimeSpan.FromMinutes(10));

        // Not eligible: FreshUntil too far in the future (outside refreshLead).
        await SeedAsync(store, "too-fresh", freshUntil: Now + TimeSpan.FromMinutes(30), lastRequestedAt: Now - TimeSpan.FromMinutes(10));

        // Not eligible: last requested outside the active window.
        await SeedAsync(store, "inactive", freshUntil: Now + TimeSpan.FromMinutes(3), lastRequestedAt: Now - TimeSpan.FromHours(2));

        var candidates = await store.GetRefreshCandidatesAsync(Now, activeWindow, refreshLead);

        var keys = candidates.Select(c => c.QueryKey).ToList();
        Assert.Contains("eligible", keys);
        Assert.DoesNotContain("too-fresh", keys);
        Assert.DoesNotContain("inactive", keys);
    }

    private static async Task SeedAsync(SearchResultCacheStore store, string key, DateTimeOffset freshUntil, DateTimeOffset lastRequestedAt)
    {
        await store.SaveAsync(key, "payload", Now - TimeSpan.FromMinutes(10), freshUntil, Now + TimeSpan.FromHours(1));
        await store.TouchLastRequestedAsync(key, lastRequestedAt);
    }

    /// <summary>
    /// Security-m3 MEDIUM #3: two concurrent cache misses for the same key both see no existing row
    /// (each via its own <see cref="ArbitarrDbContext"/>/tracker, as separate requests would) and
    /// both attempt an Add. The second <see cref="SearchResultCacheStore.SaveAsync"/> call must not
    /// surface the unique-index violation as an exception -- it should recover by updating the row
    /// the first call already inserted. LastRequestedAt must be preserved (M3-8a: SaveAsync never
    /// touches it), simulated here by stamping it directly against the "winner" context before the
    /// "loser" saves, so a regression that overwrote it on the retry path would be caught.
    /// </summary>
    [Fact]
    public async Task SaveAsync_ConcurrentInsertRace_RecoversWithoutThrowing_AndPreservesLastRequestedAt()
    {
        const string key = "series:race-key";
        var stampedAt = Now + TimeSpan.FromMinutes(1);

        using (var migrationContext = CreateContext())
        {
            migrationContext.Database.Migrate();
        }

        // Simulate the race window: both contexts read null for the key before either writes.
        using var winnerContext = CreateContext();
        using var loserContext = CreateContext();
        var winnerStore = new SearchResultCacheStore(winnerContext);
        var loserStore = new SearchResultCacheStore(loserContext);

        Assert.Null(await winnerContext.SearchResultCacheEntries.SingleOrDefaultAsync(e => e.QueryKey == key));
        Assert.Null(await loserContext.SearchResultCacheEntries.SingleOrDefaultAsync(e => e.QueryKey == key));

        // Winner inserts first and its row is what a caller then serves (stamping LastRequestedAt).
        await winnerStore.SaveAsync(key, "payload-winner", Now, Now + TimeSpan.FromMinutes(5), Now + TimeSpan.FromHours(1));
        await winnerStore.TouchLastRequestedAsync(key, stampedAt);

        // Loser's DbContext still has no tracked row for this key (it never saw the winner's insert),
        // so it attempts its own Add and must hit + recover from the unique-index violation.
        var exception = await Record.ExceptionAsync(() =>
            loserStore.SaveAsync(key, "payload-loser", Now + TimeSpan.FromSeconds(1), Now + TimeSpan.FromMinutes(6), Now + TimeSpan.FromHours(1)));

        Assert.Null(exception);

        using var verifyContext = CreateContext();
        var verifyStore = new SearchResultCacheStore(verifyContext);
        var reloaded = await verifyStore.GetAsync(key);

        Assert.NotNull(reloaded);
        Assert.Equal("payload-loser", reloaded!.PayloadJson);
        Assert.Equal(stampedAt, reloaded.LastRequestedAt);
    }
}
