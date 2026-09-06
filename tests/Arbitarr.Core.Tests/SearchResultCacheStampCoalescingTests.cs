using Arbitarr.Core.Caching;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Covers the LastRequestedAt write-coalescing rule (#23): a servable read re-stamps only once the
/// stored stamp has aged past the coalescing window, so read volume stops translating one-for-one
/// into SQLite writes. Counts calls through <see cref="ISearchResultCacheStore"/> rather than
/// asserting on the stamp value alone -- the write itself is what is being suppressed, and a
/// suppressed write is indistinguishable from a write of the same value if you only inspect state.
/// </summary>
public sealed class SearchResultCacheStampCoalescingTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);
    private const string QueryKey = "series:12345:s01e01:category=5000:profile=hd";

    private sealed class CountingStore : ISearchResultCacheStore
    {
        private readonly Dictionary<string, CachedSearchResult> _entries = new();

        public int TouchCalls { get; private set; }

        public IReadOnlyDictionary<string, CachedSearchResult> Entries => _entries;

        public Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries.TryGetValue(queryKey, out var entry) ? entry : null);

        public Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default)
        {
            var previousStamp = _entries.TryGetValue(queryKey, out var existing) ? existing.LastRequestedAt : default;
            _entries[queryKey] = new CachedSearchResult(queryKey, payloadJson, fetchedAt, freshUntil, serveUntil, previousStamp);
            return Task.CompletedTask;
        }

        public Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
        {
            TouchCalls++;
            if (_entries.TryGetValue(queryKey, out var entry))
            {
                _entries[queryKey] = entry with { LastRequestedAt = requestedAt };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CachedSearchResult>>(Array.Empty<CachedSearchResult>());

        /// <summary>Places an entry with an arbitrary stamp, bypassing the save/serve path.</summary>
        public void Seed(CachedSearchResult entry) => _entries[entry.QueryKey] = entry;
    }

    private static (SearchResultCache Cache, CountingStore Store, FakeTimeProvider Clock) Create(TimeSpan? window = null)
    {
        var clock = new FakeTimeProvider(Start);
        var store = new CountingStore();
        return (new SearchResultCache(store, clock, window ?? Window), store, clock);
    }

    /// <summary>
    /// Seeds an entry and takes the first hit. A brand-new entry carries a default(DateTimeOffset)
    /// stamp, so that hit always writes -- leaving a fresh stamp for the coalescing assertions.
    /// </summary>
    private static async Task<(SearchResultCache Cache, CountingStore Store, FakeTimeProvider Clock)> CreateStampedAsync(TimeSpan? window = null)
    {
        var (cache, store, clock) = Create(window);
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        await cache.GetAsync(QueryKey);
        Assert.Equal(1, store.TouchCalls);
        return (cache, store, clock);
    }

    [Fact]
    public async Task FreshHit_InsideWindow_IssuesNoWrite()
    {
        var (cache, store, clock) = await CreateStampedAsync();
        var stampedAt = store.Entries[QueryKey].LastRequestedAt;

        clock.Advance(Window - TimeSpan.FromSeconds(1));
        var result = await cache.GetAsync(QueryKey);

        // Coalescing suppresses the write, never the payload.
        Assert.Equal(CacheBand.Fresh, result.Band);
        Assert.Equal("payload-v1", result.PayloadJson);
        Assert.Equal(1, store.TouchCalls);
        Assert.Equal(stampedAt, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public async Task FreshHit_AtWindowBoundary_IssuesTheWrite()
    {
        var (cache, store, clock) = await CreateStampedAsync();

        clock.Advance(Window);
        var expectedStamp = clock.GetUtcNow();
        await cache.GetAsync(QueryKey);

        Assert.Equal(2, store.TouchCalls);
        Assert.Equal(expectedStamp, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public async Task StaleButValidHit_InsideWindow_IssuesNoWrite_ButStillTriggersRefresh()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(10)); // past FreshUntil, before ServeUntil
        await cache.GetAsync(QueryKey);
        Assert.Equal(1, store.TouchCalls);

        clock.Advance(Window - TimeSpan.FromSeconds(1));
        var refreshCalled = false;
        var result = await cache.GetAsync(QueryKey, () => refreshCalled = true);

        Assert.Equal(CacheBand.StaleButValid, result.Band);
        Assert.Equal(1, store.TouchCalls);
        // The refresh trigger keys off the band, not off whether the stamp was written.
        Assert.True(refreshCalled);
        Assert.True(result.RefreshTriggered);
    }

    [Fact]
    public async Task StaleButValidHit_AfterWindow_IssuesTheWrite()
    {
        var (cache, store, clock) = Create();
        await cache.SaveAsync(QueryKey, "payload-v1", TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));

        clock.Advance(TimeSpan.FromMinutes(10));
        await cache.GetAsync(QueryKey);

        clock.Advance(Window);
        var expectedStamp = clock.GetUtcNow();
        var result = await cache.GetAsync(QueryKey);

        Assert.Equal(CacheBand.StaleButValid, result.Band);
        Assert.Equal(2, store.TouchCalls);
        Assert.Equal(expectedStamp, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public async Task ManyHitsInsideWindow_CollapseToOneWrite()
    {
        var (cache, store, clock) = await CreateStampedAsync();

        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await cache.GetAsync(QueryKey);
        }

        // 20 reads spanning 20s of a 30s window: the read path stays write-free (#23).
        Assert.Equal(1, store.TouchCalls);
    }

    [Fact]
    public async Task ZeroWindow_RestoresStampOnEveryHit()
    {
        var (cache, store, clock) = await CreateStampedAsync(TimeSpan.Zero);

        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetAsync(QueryKey);

        Assert.Equal(2, store.TouchCalls);
    }

    [Fact]
    public async Task ExpiredHit_StillIssuesNoWrite()
    {
        var (cache, store, clock) = await CreateStampedAsync();
        var stampedAt = store.Entries[QueryKey].LastRequestedAt;

        clock.Advance(TimeSpan.FromHours(2)); // past ServeUntil, and well past the window
        var result = await cache.GetAsync(QueryKey);

        // M3-8a outranks coalescing: an unservable read never stamps, however old the stamp is.
        Assert.Equal(CacheBand.Expired, result.Band);
        Assert.Equal(1, store.TouchCalls);
        Assert.Equal(stampedAt, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public async Task StampInTheFuture_IsRewrittenRatherThanSuppressedIndefinitely()
    {
        var (cache, store, _) = Create();

        // What a backwards clock step (NTP correction, DST-mishandling host) leaves behind: a stamp
        // ahead of now, so now - LastRequestedAt is negative. Negative is "< window", so a naive
        // comparison would suppress the stamp until real time caught back up -- hours, potentially.
        store.Seed(new CachedSearchResult(
            QueryKey,
            "payload-v1",
            FetchedAt: Start,
            FreshUntil: Start + TimeSpan.FromMinutes(5),
            ServeUntil: Start + TimeSpan.FromHours(1),
            LastRequestedAt: Start + TimeSpan.FromMinutes(5)));

        await cache.GetAsync(QueryKey);

        Assert.Equal(1, store.TouchCalls);
        Assert.Equal(Start, store.Entries[QueryKey].LastRequestedAt);
    }

    [Fact]
    public void DefaultWindow_IsWellInsideTheRefreshSelectionWindow()
    {
        // The stamp only has to be precise enough for GetRefreshCandidatesAsync's ActiveWindow
        // comparison. If this ever inverted, a continuously-served entry could age out of refresh
        // selection between stamps -- the one way coalescing could change observable behaviour.
        Assert.True(SearchResultCache.DefaultStampCoalescingWindow < RefreshWorkerDefaults.ActiveWindow);
        Assert.Equal(RefreshWorkerDefaults.WorkerCycleInterval / 2, SearchResultCache.DefaultStampCoalescingWindow);
    }

    [Fact]
    public void NegativeWindow_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SearchResultCache(new CountingStore(), new FakeTimeProvider(Start), TimeSpan.FromSeconds(-1)));
    }
}
