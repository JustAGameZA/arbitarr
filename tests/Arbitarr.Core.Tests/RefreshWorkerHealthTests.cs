using Arbitarr.Core.Caching;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Validates that <see cref="RefreshWorker"/> reports real cycle outcomes into an injected
/// <see cref="IRefreshWorkerHealth"/> sink (M7-7, R20) — the snapshot the dashboard's
/// <c>/api/status</c> worker block now reflects instead of the pre-M3 placeholder.
/// </summary>
public sealed class RefreshWorkerHealthTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private const string SourceName = "test-source";

    private sealed class FakeStore : ISearchResultCacheStore
    {
        public readonly Dictionary<string, CachedSearchResult> Entries = new();
        public IReadOnlyList<CachedSearchResult> CandidatesToReturn = Array.Empty<CachedSearchResult>();

        public Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Entries.TryGetValue(queryKey, out var entry) ? entry : null);

        public Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default)
        {
            Entries[queryKey] = new CachedSearchResult(queryKey, payloadJson, fetchedAt, freshUntil, serveUntil, LastRequestedAt: default);
            return Task.CompletedTask;
        }

        public Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
            => Task.FromResult(CandidatesToReturn);
    }

    private sealed class FakeBreaker : IAsyncCircuitBreaker
    {
        public bool AllowCalls = true;

        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) => Task.FromResult(AllowCalls);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static RefreshWorkerOptions Options() => new(
        Enabled: true,
        WorkerCycleInterval: TimeSpan.FromMinutes(1),
        ActiveWindow: TimeSpan.FromHours(1),
        RefreshLead: TimeSpan.FromMinutes(5),
        FreshUntilAge: TimeSpan.FromMinutes(5),
        ServeUntilAge: TimeSpan.FromHours(1),
        RepopulationSpreadWindow: TimeSpan.Zero,
        MaxConcurrentRefreshes: 4);

    private static CachedSearchResult MakeEntry(string key) => new(
        QueryKey: key,
        PayloadJson: "stale-payload",
        FetchedAt: Start - TimeSpan.FromMinutes(10),
        FreshUntil: Start - TimeSpan.FromMinutes(5),
        ServeUntil: Start + TimeSpan.FromMinutes(55),
        LastRequestedAt: Start - TimeSpan.FromMinutes(1));

    [Fact]
    public async Task RunCycleAsync_NoCandidates_RecordsStartedAndCompletedWithZeroCounts()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore();
        var breaker = new FakeBreaker();
        var health = new RefreshWorkerHealthTracker();
        RefreshFetcher fetcher = (_, _, _) => Task.FromResult<string?>("new");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName, health: health);

        await worker.RunCycleAsync();

        var snapshot = health.Snapshot;
        Assert.Equal(Start, snapshot.LastCycleStartedUtc);
        Assert.Equal(Start, snapshot.LastCycleCompletedUtc);
        Assert.Equal(0, snapshot.LastCycleCandidates);
        Assert.Equal(0, snapshot.LastCycleRefreshed);
        Assert.Equal(0, snapshot.LastCycleFailed);
        Assert.Null(snapshot.LastError);
        Assert.Equal(0, snapshot.ConsecutiveFailedCycles);
    }

    [Fact]
    public async Task RunCycleAsync_BreakerOpen_RecordsCandidatesButNoRefreshes()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = new[] { MakeEntry("key-1") } };
        var breaker = new FakeBreaker { AllowCalls = false };
        var health = new RefreshWorkerHealthTracker();
        RefreshFetcher fetcher = (_, _, _) => Task.FromResult<string?>("new");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName, health: health);

        await worker.RunCycleAsync();

        var snapshot = health.Snapshot;
        Assert.Equal(1, snapshot.LastCycleCandidates);
        Assert.Equal(0, snapshot.LastCycleRefreshed);
        Assert.Equal(0, snapshot.LastCycleFailed);
    }

    [Fact]
    public async Task RunCycleAsync_SuccessfulRefresh_RecordsRefreshedCount()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = new[] { MakeEntry("key-1") } };
        var breaker = new FakeBreaker();
        var health = new RefreshWorkerHealthTracker();
        RefreshFetcher fetcher = (_, entry, _) => Task.FromResult<string?>(entry.PayloadJson + "-refreshed");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName, health: health);

        await worker.RunCycleAsync();

        var snapshot = health.Snapshot;
        Assert.Equal(1, snapshot.LastCycleCandidates);
        Assert.Equal(1, snapshot.LastCycleRefreshed);
        Assert.Equal(0, snapshot.LastCycleFailed);
        Assert.Equal(0, snapshot.ConsecutiveFailedCycles);
    }

    [Fact]
    public async Task RunCycleAsync_FetcherThrows_RecordsFailedCount_NotCycleFault()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = new[] { MakeEntry("key-1") } };
        var breaker = new FakeBreaker();
        var health = new RefreshWorkerHealthTracker();
        RefreshFetcher fetcher = (_, _, _) => throw new InvalidOperationException("upstream call failed");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName, health: health);

        await worker.RunCycleAsync();

        var snapshot = health.Snapshot;
        Assert.Equal(1, snapshot.LastCycleCandidates);
        Assert.Equal(0, snapshot.LastCycleRefreshed);
        Assert.Equal(1, snapshot.LastCycleFailed);
        // Per-entry failure is not a cycle-level fault (the cycle itself completed normally).
        Assert.Null(snapshot.LastError);
        Assert.Equal(0, snapshot.ConsecutiveFailedCycles);
    }

    /// <summary>A store that always throws out of GetRefreshCandidatesAsync — the cycle-level fault path.</summary>
    private sealed class ThrowingStore : ISearchResultCacheStore
    {
        public Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default)
            => Task.FromResult<CachedSearchResult?>(null);

        public Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no such table: SearchResultCacheEntries");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowingCycle_RecordsCycleFault_MessageOnly()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new ThrowingStore();
        var breaker = new FakeBreaker();
        var health = new RefreshWorkerHealthTracker();
        RefreshFetcher fetcher = (_, _, _) => Task.FromResult<string?>("new");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName, health: health);

        await worker.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (health.Snapshot.LastError is null && DateTime.UtcNow < deadline)
        {
            clock.Advance(Options().WorkerCycleInterval);
            await Task.Delay(10);
        }

        await worker.StopAsync(CancellationToken.None);

        var snapshot = health.Snapshot;
        Assert.Equal("no such table: SearchResultCacheEntries", snapshot.LastError);
        Assert.True(snapshot.ConsecutiveFailedCycles >= 1);
    }
}
