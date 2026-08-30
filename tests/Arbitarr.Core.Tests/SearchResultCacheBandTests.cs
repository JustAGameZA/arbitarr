using Arbitarr.Core.Caching;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Validates the two-age cache read path (plan Step 4a Step 2): band classification (M3-1/M3-2)
/// and the LastRequestedAt stamping rule — stamped only when an entry is actually served, never
/// on Expired-band reads or misses (Architect A1, M3-8a). Driven against a genuine in-memory fake
/// <see cref="ISearchResultCacheStore"/> and a <see cref="FakeTimeProvider"/> whose clock is
/// explicitly advanced across bands, never asserted on internal state without exercising real
/// reads/writes through <see cref="SearchResultCache"/>.
/// </summary>
public sealed class SearchResultCacheBandTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private const string QueryKey = "series:12345:s01e01:category=5000:profile=hd";

    private sealed class FakeStore : ISearchResultCacheStore
    {
        private readonly Dictionary<string, CachedSearchResult> _entries = new();

        public IReadOnlyDictionary<string, CachedSearchResult> Entries => _entries;

        public Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries.TryGetValue(queryKey, out var entry) ? entry : null);

        public Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default)
        {
            // SearchResultCache.SaveAsync's contract is to never touch LastRequestedAt: preserve
            // whatever was already stored for this key, matching real store semantics.
            var previousStamp = _entries.TryGetValue(queryKey, out var existing) ? existing.LastRequestedAt : default;
            _entries[queryKey] = new CachedSearchResult(queryKey, payloadJson, fetchedAt, freshUntil, serveUntil, previousStamp);
            return Task.CompletedTask;
        }

        public Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
        {
            if (_entries.TryGetValue(queryKey, out var entry))
            {
                _entries[queryKey] = entry with { LastRequestedAt = requestedAt };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CachedSearchResult>>(Array.Empty<CachedSearchResult>());
    }

    private static (SearchResultCache Cache, FakeStore Store, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore();
        var cache = new SearchResultCache(store, clock);
        return (cache, store, clock);
    }

    [Fact]
    public void Classify_ReturnsFresh_BeforeFreshUntil()
    {
        var band = SearchResultCache.Classify(Start, freshUntil: Start + TimeSpan.FromMinutes(5), serveUntil: Start + TimeSpan.FromHours(1));
        Assert.Equal(CacheBand.Fresh, band);
    }

    [Fact]
    public void Classify_ReturnsStaleButValid_BetweenFreshUntilAndServeUntil()
    {
        var now = Start + TimeSpan.FromMinutes(10);
        var band = SearchResultCache.Classify(now, freshUntil: Start + TimeSpan.FromMinutes(5), serveUntil: Start + TimeSpan.FromHours(1));
        Assert.Equal(CacheBand.StaleButValid, band);
    }

    [Fact]
    public void Classify_ReturnsExpired_AtOrAfterServeUntil()
    {
        var now = Start + TimeSpan.FromHours(1);
        var band = SearchResultCache.Classify(now, freshUntil: Start + TimeSpan.FromMinutes(5), serveUntil: Start + TimeSpan.FromHours(1));
        Assert.Equal(CacheBand.Expired, band);
    }

    [Fact]
    public async Task GetAsync_Fresh_ServesDirectly_WithNoRefreshTriggered()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(1));

        var refreshCalled = false;
        var result = await cache.GetAsync(QueryKey, () => refreshCalled = true);

        Assert.Equal(CacheBand.Fresh, result.Band);
        Assert.True(result.IsServable);
        Assert.Equal("payload-v1", result.PayloadJson);
        Assert.False(result.RefreshTriggered);
        Assert.False(refreshCalled);
    }

    [Fact]
    public async Task GetAsync_Fresh_StampsLastRequestedAt()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(1));
        var expectedStamp = clock.GetUtcNow();
        await cache.GetAsync(QueryKey);

        Assert.Equal(expectedStamp, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public async Task GetAsync_StaleButValid_ServesImmediately_AndTriggersRefresh()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(10)); // past FreshUntil, before ServeUntil

        var refreshCalled = false;
        var result = await cache.GetAsync(QueryKey, () => refreshCalled = true);

        Assert.Equal(CacheBand.StaleButValid, result.Band);
        Assert.True(result.IsServable);
        Assert.Equal("payload-v1", result.PayloadJson);
        Assert.True(result.RefreshTriggered);
        Assert.True(refreshCalled);
    }

    [Fact]
    public async Task GetAsync_StaleButValid_StampsLastRequestedAt()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(10));
        var expectedStamp = clock.GetUtcNow();
        await cache.GetAsync(QueryKey);

        Assert.Equal(expectedStamp, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public async Task GetAsync_Expired_DoesNotServe_AndDoesNotStampLastRequestedAt()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        // Stamp LastRequestedAt once while still servable, so we can prove Expired reads leave it alone.
        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.GetAsync(QueryKey);
        var stampBeforeExpiry = store.Entries[QueryKey].LastRequestedAt;

        clock.Advance(TimeSpan.FromHours(2)); // past ServeUntil

        var refreshCalled = false;
        var result = await cache.GetAsync(QueryKey, () => refreshCalled = true);

        Assert.Equal(CacheBand.Expired, result.Band);
        Assert.False(result.IsServable);
        Assert.Null(result.PayloadJson);
        Assert.False(result.RefreshTriggered);
        Assert.False(refreshCalled);
        Assert.Equal(stampBeforeExpiry, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public async Task GetAsync_MissingEntry_ReportsExpiredWithNullPayload()
    {
        var (cache, _, _) = Create();

        var result = await cache.GetAsync("no-such-key");

        Assert.Equal(CacheBand.Expired, result.Band);
        Assert.False(result.IsServable);
        Assert.Null(result.Age);
    }

    [Fact]
    public async Task SaveAsync_NeverTouchesLastRequestedAt()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.GetAsync(QueryKey); // stamps LastRequestedAt
        var stampedAt = store.Entries[QueryKey].LastRequestedAt;

        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.SaveAsync(QueryKey, "payload-v2", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        Assert.Equal(stampedAt, store.Entries[QueryKey].LastRequestedAt);
        Assert.Equal("payload-v2", store.Entries[QueryKey].PayloadJson);
    }
}
