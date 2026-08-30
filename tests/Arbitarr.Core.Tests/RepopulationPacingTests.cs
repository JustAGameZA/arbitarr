using Arbitarr.Core.Caching;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Validates R22's re-population admission pacing (M3-4): a backlog of refresh candidates must be
/// spread across a full <c>fresh_until</c> interval (not fired as a synchronized burst), and
/// per-source in-flight refreshes must never exceed the configured bound. Asserted against
/// observed start timestamps recorded as each refresh actually begins, per the plan's non-vacuous
/// proof bar — never against planned offsets alone.
/// </summary>
public sealed class RepopulationPacingTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private const string SourceName = "test-source";

    [Fact]
    public void Plan_SpreadsOffsets_AcrossFullWindow()
    {
        var pacer = new RepopulationPacer(new Random(7));
        var candidates = Enumerable.Range(0, 200)
            .Select(i => MakeEntry($"key-{i}"))
            .ToList();

        var spreadWindow = TimeSpan.FromMinutes(30);
        var plan = pacer.Plan(candidates, spreadWindow, maxConcurrent: 4, SourceName);

        Assert.Equal(candidates.Count, plan.Count);
        Assert.All(plan, p => Assert.InRange(p.StartOffset, TimeSpan.Zero, spreadWindow));

        // With 200 independently-random offsets across the window, at least one must land in the
        // final quarter of the window -- proof the spread genuinely covers (not clusters short of)
        // a full fresh_until interval, rather than merely respecting the upper bound.
        Assert.Contains(plan, p => p.StartOffset >= spreadWindow * 0.75);
    }

    [Fact]
    public void Plan_IsSorted_ByStartOffset()
    {
        var pacer = new RepopulationPacer(new Random(3));
        var candidates = Enumerable.Range(0, 20).Select(i => MakeEntry($"key-{i}")).ToList();

        var plan = pacer.Plan(candidates, TimeSpan.FromMinutes(15), maxConcurrent: 2, SourceName);

        var offsets = plan.Select(p => p.StartOffset).ToList();
        var sorted = offsets.OrderBy(o => o).ToList();
        Assert.Equal(sorted, offsets);
    }

    [Fact]
    public void Plan_EmptyCandidates_ReturnsEmptyPlan()
    {
        var pacer = new RepopulationPacer();
        var plan = pacer.Plan(Array.Empty<CachedSearchResult>(), TimeSpan.FromMinutes(15), maxConcurrent: 2, SourceName);
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_RejectsNonPositiveMaxConcurrent()
    {
        var pacer = new RepopulationPacer();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            pacer.Plan(new[] { MakeEntry("key-1") }, TimeSpan.FromMinutes(15), maxConcurrent: 0, SourceName));
    }

    private sealed class FakeStore : ISearchResultCacheStore
    {
        // M3-fix7: RefreshWorker.RunCycleAsync runs refreshes concurrently (bounded by
        // MaxConcurrentRefreshes), so SaveAsync can be invoked from multiple tasks at once. A plain
        // Dictionary is not thread-safe under concurrent writes and can silently drop an entry,
        // making the test's final Entries.Count flaky. ConcurrentDictionary makes it deterministic.
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedSearchResult> Entries = new();
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

    private sealed class AlwaysOpenBreaker : IAsyncCircuitBreaker
    {
        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static CachedSearchResult MakeEntry(string key) => new(
        QueryKey: key,
        PayloadJson: "stale-payload",
        FetchedAt: Start - TimeSpan.FromMinutes(10),
        FreshUntil: Start - TimeSpan.FromMinutes(5),
        ServeUntil: Start + TimeSpan.FromMinutes(55),
        LastRequestedAt: Start - TimeSpan.FromMinutes(1));

    [Fact]
    public async Task RunCycleAsync_NeverExceedsMaxConcurrentRefreshes_AgainstObservedStarts()
    {
        var clock = new FakeTimeProvider(Start);
        var candidates = Enumerable.Range(0, 12).Select(i => MakeEntry($"key-{i}")).ToList();
        var store = new FakeStore { CandidatesToReturn = candidates };
        var breaker = new AlwaysOpenBreaker();

        const int maxConcurrent = 3;
        var inFlight = 0;
        var maxObservedInFlight = 0;
        var gate = new object();

        RefreshFetcher fetcher = async (_, entry, ct) =>
        {
            lock (gate)
            {
                inFlight++;
                maxObservedInFlight = Math.Max(maxObservedInFlight, inFlight);
            }

            // Hold the slot briefly so overlapping starts would be observed if the bound were violated.
            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);

            lock (gate)
            {
                inFlight--;
            }

            return entry.PayloadJson + "-refreshed";
        };

        var options = new RefreshWorkerOptions(
            Enabled: true,
            WorkerCycleInterval: TimeSpan.FromMinutes(1),
            ActiveWindow: TimeSpan.FromHours(1),
            RefreshLead: TimeSpan.FromMinutes(5),
            FreshUntilAge: TimeSpan.FromMinutes(5),
            ServeUntilAge: TimeSpan.FromHours(1),
            RepopulationSpreadWindow: TimeSpan.Zero, // isolate the concurrency bound from the spread
            MaxConcurrentRefreshes: maxConcurrent);

        var worker = new RefreshWorker(store, new SearchResultCache(store, clock), breaker, fetcher, clock, options, SourceName, new RepopulationPacer(new Random(1)));

        await worker.RunCycleAsync();

        Assert.True(maxObservedInFlight <= maxConcurrent, $"observed {maxObservedInFlight} in flight, bound was {maxConcurrent}");
        Assert.Equal(candidates.Count, store.Entries.Count);
    }
}
