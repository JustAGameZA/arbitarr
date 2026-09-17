using System.Net;
using System.Net.Sockets;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
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
        // arb-mhd2: the outcome is cleared with the message, never left behind to publish a
        // failure reason for a cycle that succeeded.
        Assert.Equal(SourceStatusOutcome.None, snapshot.LastOutcome);
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

        public Func<Exception> ExceptionFactory { get; init; } =
            () => new InvalidOperationException("no such table: SearchResultCacheEntries");

        public Task<IReadOnlyList<CachedSearchResult>> GetRefreshCandidatesAsync(DateTimeOffset now, TimeSpan activeWindow, TimeSpan refreshLead, CancellationToken cancellationToken = default)
            => throw ExceptionFactory();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowingCycle_RecordsCycleFault_SanitizedDescriptionOnly()
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

        // POSITIVE CONTROL (CLAUDE.md section 4). The DoesNotContain below passes just as
        // happily when the marker was never in play. This proves the marker was really in
        // the exception the worker caught, so the absence assertion is over a set the
        // marker COULD have reached rather than over an empty one.
        var raw = store.ExceptionFactory().Message;
        Assert.Contains("no such table", raw, StringComparison.Ordinal);

        // The raw message is never surfaced: /api/status is unauthenticated, so only the
        // sanitized type-name description reaches the worker health snapshot.
        Assert.Equal(nameof(InvalidOperationException), snapshot.LastError);
        Assert.DoesNotContain("no such table", snapshot.LastError!, StringComparison.Ordinal);
        Assert.True(snapshot.ConsecutiveFailedCycles >= 1);

        // arb-mhd2: the closed outcome is written from the same exception, in the same call.
        // InvalidOperationException is no HTTP family, so it classifies as InternalError.
        Assert.Equal(SourceStatusOutcome.InternalError, snapshot.LastOutcome);
    }

    /// <summary>
    /// Regression: a cycle fault whose exception message embeds the upstream host (the shape
    /// HttpRequestException takes on a DNS/connect failure) must never reach the worker health
    /// snapshot, which <c>GET /api/status</c> serves unauthenticated. Leaking it would disclose
    /// LAN topology to any caller that can reach the dashboard.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CycleFaultWithHostBearingMessage_DoesNotLeakUpstreamHost()
    {
        // A documentation host and an arbitrary port, never a real deployment's: this file is
        // public, and a fixture that names a real LAN host discloses exactly what the test exists
        // to prove is not disclosed.
        const string UpstreamHost = "indexer.example.invalid";
        const string UpstreamPort = "5076";
        const string HostBearingMessage = $"No such host is known. ({UpstreamHost}:{UpstreamPort})";

        var clock = new FakeTimeProvider(Start);
        var store = new ThrowingStore
        {
            ExceptionFactory = () => new HttpRequestException(HostBearingMessage, inner: null, System.Net.HttpStatusCode.BadGateway),
        };
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

        var lastError = health.Snapshot.LastError;
        Assert.NotNull(lastError);

        // POSITIVE CONTROL (CLAUDE.md section 4). Asserting the snapshot is non-null proves the
        // fault path ran; it does NOT prove the host would have been detectable had it leaked.
        // These three assert the marker really was in the message the worker sanitized, so the
        // three DoesNotContain below cannot pass vacuously.
        Assert.Contains(UpstreamHost, HostBearingMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(UpstreamPort, HostBearingMessage, StringComparison.Ordinal);
        Assert.Contains("No such host", HostBearingMessage, StringComparison.Ordinal);

        Assert.DoesNotContain(UpstreamHost, lastError!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UpstreamPort, lastError!, StringComparison.Ordinal);
        Assert.DoesNotContain("No such host", lastError!, StringComparison.Ordinal);
        // The status code survives sanitization; it is diagnostic without being topological.
        Assert.Equal("HttpRequestException (502 BadGateway)", lastError);

        // arb-mhd2: an HttpRequestException carrying a status code is an upstream error, not an
        // unreachable host -- the status proves we reached something that answered.
        Assert.Equal(SourceStatusOutcome.UpstreamError, health.Snapshot.LastOutcome);
    }

    /// <summary>
    /// arb-mhd2: the worker's own writer, one row per exception family. The tracker is driven
    /// directly rather than through a cycle, so there is no polling loop and no <c>Task.Delay</c>
    /// standing in for synchronisation -- the two writers are asserted on the same vocabulary, and
    /// this is the half that says the WORKER's fault path agrees with the breaker's.
    /// </summary>
    public static TheoryData<Exception, SourceStatusOutcome> WorkerFailureFamilies() => new()
    {
        { new HttpRequestException("denied", null, HttpStatusCode.Unauthorized), SourceStatusOutcome.AuthRejected },
        { new HttpRequestException("denied", null, HttpStatusCode.Forbidden), SourceStatusOutcome.AuthRejected },
        { new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway), SourceStatusOutcome.UpstreamError },
        { new HttpRequestException("no such host"), SourceStatusOutcome.Unreachable },
        { new SocketException(10061), SourceStatusOutcome.Unreachable },
        { new TaskCanceledException("timed out"), SourceStatusOutcome.Timeout },
        { new TimeoutException("timed out"), SourceStatusOutcome.Timeout },
        { new InvalidOperationException("no such table"), SourceStatusOutcome.InternalError },
    };

    [Theory]
    [MemberData(nameof(WorkerFailureFamilies))]
    public void CycleFaulted_RecordsTheClosedOutcomeForEachExceptionFamily(
        Exception ex,
        SourceStatusOutcome expected)
    {
        var health = new RefreshWorkerHealthTracker();

        // The outcome and the sanitized text are supplied together, from one exception, because they
        // describe one failure -- CycleFaulted takes both as required parameters so no caller can
        // set one without the other.
        health.CycleFaulted(
            Start,
            SanitizedErrorDescription.Describe(ex),
            SourceStatusOutcomeClassifier.Classify(ex));

        var snapshot = health.Snapshot;
        Assert.Equal(expected, snapshot.LastOutcome);
        Assert.NotNull(snapshot.LastError);
        Assert.NotEqual(SourceStatusOutcome.Unknown, snapshot.LastOutcome);
    }

    /// <summary>
    /// arb-mhd2: <c>Unknown</c> is unreachable from the worker writer too. Worker health is
    /// in-memory and never persisted, so there is no stored-name path that could produce it here at
    /// all -- which is exactly why a live writer producing one would mean the classifier had grown a
    /// hole rather than that an old row had been read.
    /// </summary>
    [Fact]
    public void CycleFaulted_NeverRecordsUnknown_ForAnyExceptionFamily()
    {
        var families = WorkerFailureFamilies().ToList();
        var recorded = new List<SourceStatusOutcome>();

        foreach (var row in families)
        {
            var ex = (Exception)row[0]!;
            var health = new RefreshWorkerHealthTracker();
            health.CycleFaulted(
                Start,
                SanitizedErrorDescription.Describe(ex),
                SourceStatusOutcomeClassifier.Classify(ex));
            recorded.Add(health.Snapshot.LastOutcome);
        }

        // Positive control: the sweep ran over every family, so the absences below are not
        // assertions over an empty list.
        Assert.Equal(families.Count, recorded.Count);
        Assert.NotEmpty(recorded);
        Assert.DoesNotContain(SourceStatusOutcome.Unknown, recorded);
        Assert.DoesNotContain(SourceStatusOutcome.None, recorded);
    }

    /// <summary>
    /// arb-mhd2: a completed cycle clears the outcome along with the message. Asserted after a
    /// fault, so the clearing is a real transition rather than a field that was never set.
    /// </summary>
    [Fact]
    public void CycleCompleted_ClearsTheOutcomeRecordedByAPriorFault()
    {
        var health = new RefreshWorkerHealthTracker();
        health.CycleFaulted(Start, "HttpRequestException (502 BadGateway)", SourceStatusOutcome.UpstreamError);

        // Positive control for the clearing below.
        Assert.Equal(SourceStatusOutcome.UpstreamError, health.Snapshot.LastOutcome);
        Assert.NotNull(health.Snapshot.LastError);

        health.CycleStarted(Start, enabled: true, candidateCount: 0);
        health.CycleCompleted(Start, refreshed: 0, failed: 0);

        Assert.Equal(SourceStatusOutcome.None, health.Snapshot.LastOutcome);
        Assert.Null(health.Snapshot.LastError);
    }
}
