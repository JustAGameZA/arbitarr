using Arbitarr.Core.Caching;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// M7-8b/AC24: <see cref="RefreshWorker"/> must re-read its <see cref="RefreshWorkerOptions"/> from
/// the injected <see cref="IRefreshWorkerOptionsSource"/> at the start of every cycle, not capture
/// them once at construction — so a setting changed between two cycles takes effect on the very
/// next one without a restart.
/// </summary>
public sealed class RefreshWorkerOptionsLivenessTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private const string SourceName = "test-source";

    private sealed class MutableOptionsSource : IRefreshWorkerOptionsSource
    {
        public RefreshWorkerOptions Current { get; set; } = null!;

        public ValueTask<RefreshWorkerOptions> GetAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Current);
    }

    private sealed class FakeStore : ISearchResultCacheStore
    {
        public readonly Dictionary<string, CachedSearchResult> Entries = new();
        public IReadOnlyList<CachedSearchResult> CandidatesToReturn = Array.Empty<CachedSearchResult>();
        public readonly List<TimeSpan> ObservedActiveWindows = new();

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
        {
            ObservedActiveWindows.Add(activeWindow);
            return Task.FromResult(CandidatesToReturn);
        }
    }

    private sealed class FakeBreaker : IAsyncCircuitBreaker
    {
        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static RefreshWorkerOptions Options(bool enabled = true, TimeSpan? activeWindow = null) => new(
        Enabled: enabled,
        WorkerCycleInterval: TimeSpan.FromMinutes(1),
        ActiveWindow: activeWindow ?? TimeSpan.FromHours(1),
        RefreshLead: TimeSpan.FromMinutes(5),
        FreshUntilAge: TimeSpan.FromMinutes(5),
        ServeUntilAge: TimeSpan.FromHours(1),
        RepopulationSpreadWindow: TimeSpan.Zero,
        MaxConcurrentRefreshes: 4);

    private static RefreshWorker MakeWorker(
        FakeStore store,
        FakeTimeProvider clock,
        MutableOptionsSource optionsSource)
    {
        var breaker = new FakeBreaker();
        var cache = new SearchResultCache(store, clock);
        RefreshFetcher fetcher = (_, _, _) => Task.FromResult<string?>("new");

        // The fixed-deps constructor always wraps its options in a StaticRefreshWorkerOptionsSource
        // (fixed for the worker's lifetime), so exercising options liveness requires the
        // scope-factory constructor with a fake IServiceScopeFactory that resolves our mutable
        // source fresh "per cycle" (mirroring how the Host resolves it from a fresh DI scope).
        return new RefreshWorker(
            new TestScopeFactory(store, cache, breaker, fetcher, optionsSource),
            clock,
            SourceName);
    }

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/>/<see cref="IServiceProvider"/> stand-in so the
    /// scope-factory constructor (the Host-shaped one, which resolves
    /// <see cref="IRefreshWorkerOptionsSource"/> fresh per cycle) can be exercised directly against
    /// fakes, without pulling in a full DI container.
    /// </summary>
    private sealed class TestScopeFactory(
        ISearchResultCacheStore store,
        SearchResultCache cache,
        IAsyncCircuitBreaker breaker,
        RefreshFetcher fetcher,
        MutableOptionsSource optionsSource) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(store, cache, breaker, fetcher, optionsSource);

        private sealed class TestScope(
            ISearchResultCacheStore store,
            SearchResultCache cache,
            IAsyncCircuitBreaker breaker,
            RefreshFetcher fetcher,
            MutableOptionsSource optionsSource) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new TestServiceProvider(store, cache, breaker, fetcher, optionsSource);

            public void Dispose()
            {
            }
        }

        private sealed class TestServiceProvider(
            ISearchResultCacheStore store,
            SearchResultCache cache,
            IAsyncCircuitBreaker breaker,
            RefreshFetcher fetcher,
            MutableOptionsSource optionsSource) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(ISearchResultCacheStore)) return store;
                if (serviceType == typeof(SearchResultCache)) return cache;
                if (serviceType == typeof(IAsyncCircuitBreaker)) return breaker;
                if (serviceType == typeof(RefreshFetcher)) return fetcher;
                if (serviceType == typeof(IRefreshWorkerOptionsSource)) return optionsSource;
                return null;
            }
        }
    }

    [Fact]
    public async Task RunCycleAsync_ReadsOptionsFreshEachCall_NarrowedActiveWindowChangesCandidateQuery()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore();
        var optionsSource = new MutableOptionsSource { Current = Options(activeWindow: TimeSpan.FromHours(1)) };
        var worker = MakeWorker(store, clock, optionsSource);

        await worker.RunCycleAsync();
        Assert.Equal(TimeSpan.FromHours(1), store.ObservedActiveWindows[0]);

        // Change the setting between cycles: the very next RunCycleAsync must observe it.
        optionsSource.Current = Options(activeWindow: TimeSpan.FromMinutes(10));
        await worker.RunCycleAsync();

        Assert.Equal(TimeSpan.FromMinutes(10), store.ObservedActiveWindows[1]);
    }

    [Fact]
    public async Task RunCycleAsync_EnabledFalse_SkippedByExecuteAsync_WithoutQueryingCandidates()
    {
        // ExecuteAsync (not RunCycleAsync directly) is what honours Enabled; verify that toggling it
        // via the source prevents the next tick's cycle from running at all.
        var clock = new FakeTimeProvider(Start);
        var store = new FakeStore { CandidatesToReturn = Array.Empty<CachedSearchResult>() };
        var optionsSource = new MutableOptionsSource { Current = Options(enabled: true) };
        var worker = MakeWorker(store, clock, optionsSource);

        await worker.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (store.ObservedActiveWindows.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Single(store.ObservedActiveWindows);

        // Disable before the next tick.
        optionsSource.Current = Options(enabled: false);
        clock.Advance(TimeSpan.FromMinutes(1));

        // Give the loop a chance to (not) run a cycle, then advance again to confirm no further
        // candidate queries occurred while disabled.
        await Task.Delay(50);
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);

        Assert.Single(store.ObservedActiveWindows);

        await worker.StopAsync(CancellationToken.None);
    }
}
