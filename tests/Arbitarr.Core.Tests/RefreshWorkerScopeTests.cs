using Arbitarr.Core.Caching;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Validates <see cref="RefreshWorker"/>'s selection/breaker/write-back contract (plan Step 4a
/// Step 3, M3-3, M3-10): it selects only entries the store reports as refresh candidates, defers
/// entirely to the shared per-source <see cref="IAsyncCircuitBreaker"/> before attempting any
/// refresh, and on a failed refresh leaves the existing entry completely untouched.
/// </summary>
public sealed class RefreshWorkerScopeTests
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
        public int SuccessCount;
        public readonly List<Exception> Failures = new();

        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) => Task.FromResult(AllowCalls);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default)
        {
            SuccessCount++;
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default)
        {
            Failures.Add(exception);
            return Task.CompletedTask;
        }
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
    public async Task RunCycleAsync_NoCandidates_DoesNotCallFetcherOrBreaker()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore();
        var breaker = new FakeBreaker();
        var fetcherCalled = false;
        RefreshFetcher fetcher = (_, _, _) => { fetcherCalled = true; return Task.FromResult<string?>("new"); };

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName);

        await worker.RunCycleAsync();

        Assert.False(fetcherCalled);
        Assert.Equal(0, breaker.SuccessCount);
    }

    [Fact]
    public async Task RunCycleAsync_BreakerOpen_SkipsRefreshEntirely()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = new[] { MakeEntry("key-1") } };
        var breaker = new FakeBreaker { AllowCalls = false };
        var fetcherCalled = false;
        RefreshFetcher fetcher = (_, _, _) => { fetcherCalled = true; return Task.FromResult<string?>("new"); };

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName);

        await worker.RunCycleAsync();

        Assert.False(fetcherCalled);
    }

    [Fact]
    public async Task RunCycleAsync_SuccessfulRefresh_SavesPayload_AndRecordsBreakerSuccess()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = new[] { MakeEntry("key-1") } };
        var breaker = new FakeBreaker();
        RefreshFetcher fetcher = (_, entry, _) => Task.FromResult<string?>(entry.PayloadJson + "-refreshed");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName);

        await worker.RunCycleAsync();

        Assert.Equal(1, breaker.SuccessCount);
        Assert.True(store.Entries.ContainsKey("key-1"));
        Assert.Equal("stale-payload-refreshed", store.Entries["key-1"].PayloadJson);
    }

    [Fact]
    public async Task RunCycleAsync_FetcherThrows_RecordsBreakerFailure_AndLeavesEntryUntouched()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = new[] { MakeEntry("key-1") } };
        var breaker = new FakeBreaker();
        RefreshFetcher fetcher = (_, _, _) => throw new InvalidOperationException("upstream call failed");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName);

        await worker.RunCycleAsync();

        Assert.Single(breaker.Failures);
        Assert.Equal(0, breaker.SuccessCount);
        // M3-10: no write-back occurred at all — the store never received a SaveAsync call for this key.
        Assert.False(store.Entries.ContainsKey("key-1"));
    }

    [Fact]
    public async Task RunCycleAsync_FetcherReturnsNull_LeavesEntryUntouched_AndDoesNotRecordFailure()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = new[] { MakeEntry("key-1") } };
        var breaker = new FakeBreaker();
        RefreshFetcher fetcher = (_, _, _) => Task.FromResult<string?>(null);

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName);

        await worker.RunCycleAsync();

        Assert.Empty(breaker.Failures);
        Assert.Equal(0, breaker.SuccessCount);
        Assert.False(store.Entries.ContainsKey("key-1"));
    }

    /// <summary>
    /// A store that fails every selection call — stands in for the host booting against a database
    /// whose migration has not run yet, the case that must not take the whole host down.
    /// </summary>
    private sealed class ThrowingStore : ISearchResultCacheStore
    {
        public int SelectionAttempts;

        public Task<CachedSearchResult?> GetAsync(string queryKey, CancellationToken cancellationToken = default)
            => Task.FromResult<CachedSearchResult?>(null);

        public Task SaveAsync(string queryKey, string payloadJson, DateTimeOffset fetchedAt, DateTimeOffset freshUntil, DateTimeOffset serveUntil, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task TouchLastRequestedAsync(string queryKey, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SelectionAttempts);
            throw new InvalidOperationException("no such table: SearchResultCacheEntries");
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThrowingCycle_DoesNotFaultTheWorker_AndKeepsRetryingUntilStopped()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new ThrowingStore();
        var breaker = new FakeBreaker();
        RefreshFetcher fetcher = (_, _, _) => Task.FromResult<string?>("new");

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, Options(), SourceName);

        await worker.StartAsync(CancellationToken.None);

        // Drive several cycles: each throws out of the store, and each must be swallowed and retried
        // rather than faulting the BackgroundService (which would otherwise take the host down).
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(Options().WorkerCycleInterval);
            await Task.Yield();
        }

        Assert.True(store.SelectionAttempts > 1, $"expected repeated retries, saw {store.SelectionAttempts}");
        Assert.NotEqual(TaskStatus.Faulted, worker.ExecuteTask?.Status);
        Assert.Null(worker.ExecuteTask?.Exception);

        await worker.StopAsync(CancellationToken.None);

        Assert.Null(worker.ExecuteTask?.Exception);
    }
}
