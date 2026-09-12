using Arbitarr.Core.Arbitration;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// AC14b unit coverage for <see cref="SyncReleaseArbiter"/>: P1 fail-open on every exception
/// <see cref="ReleaseClassifier.TryClassifyAsync"/> also treats as fail-open, plus the
/// per-candidate <see cref="ArbitrationContext.Budget"/> timeout, which is this type's own
/// addition (not shared with <see cref="ReleaseClassifier"/>).
/// </summary>
public class SyncReleaseArbiterTests
{
    private static ReleaseCandidate Candidate(string guid) => new()
    {
        Title = "Movie.WEB",
        Guid = guid,
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    private static ArbitrationContext Context(TimeSpan? budget = null) =>
        new(budget ?? TimeSpan.FromSeconds(5));

    public static IEnumerable<object[]> FailOpenExceptions()
    {
        yield return new object[] { new OllamaCircuitOpenException() };
        yield return new object[] { new HttpRequestException("boom") };
        yield return new object[] { new TaskCanceledException() };
        yield return new object[] { new OperationCanceledException() };
    }

    [Theory]
    [MemberData(nameof(FailOpenExceptions))]
    public async Task ArbitrateAsync_FailsOpen_ToUnknown_ForEachExpectedExceptionType(Exception exception)
    {
        var arbiter = new SyncReleaseArbiter(new ThrowingOllamaClient(exception));

        var outcomes = await arbiter.ArbitrateAsync(new[] { Candidate("guid-1") }, Context(), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("guid-1", outcome.Guid);
        Assert.Equal(Verdict.Unknown, outcome.Verdict);
        Assert.Null(outcome.Confidence);
    }

    [Fact]
    public async Task ArbitrateAsync_UnexpectedException_StillPropagates()
    {
        var arbiter = new SyncReleaseArbiter(new ThrowingOllamaClient(new InvalidOperationException("unexpected")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => arbiter.ArbitrateAsync(new[] { Candidate("guid-1") }, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task ArbitrateAsync_ExceedingTheBudget_FailsOpen_ToUnknown()
    {
        // AC14b: a model call that never completes within the per-call budget must fail open rather
        // than hang the admin's ad-hoc search indefinitely.
        var arbiter = new SyncReleaseArbiter(new NeverCompletingOllamaClient());

        var outcomes = await arbiter.ArbitrateAsync(
            new[] { Candidate("guid-1") }, Context(TimeSpan.FromMilliseconds(50)), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(Verdict.Unknown, outcome.Verdict);
        Assert.Null(outcome.Confidence);
    }

    [Fact]
    public async Task ArbitrateAsync_AcceptVerdict_IsMappedFromOllamaVerdict()
    {
        var arbiter = new SyncReleaseArbiter(new StaticOllamaClient(new OllamaVerdict("accept", 0.87)));

        var outcomes = await arbiter.ArbitrateAsync(new[] { Candidate("guid-1") }, Context(), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(Verdict.Accept, outcome.Verdict);
        Assert.Equal(0.87, outcome.Confidence);
    }

    [Fact]
    public async Task ArbitrateAsync_RejectVerdict_IsMappedFromOllamaVerdict()
    {
        var arbiter = new SyncReleaseArbiter(new StaticOllamaClient(new OllamaVerdict("reject", 0.42)));

        var outcomes = await arbiter.ArbitrateAsync(new[] { Candidate("guid-1") }, Context(), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(Verdict.Reject, outcome.Verdict);
        Assert.Equal(0.42, outcome.Confidence);
    }

    [Fact]
    public async Task ArbitrateAsync_MultipleCandidates_ReturnsOneOutcomePerCandidate_InOrder_NeverRewritingIdentity()
    {
        var arbiter = new SyncReleaseArbiter(new StaticOllamaClient(new OllamaVerdict("accept", 0.5)));
        var candidates = new[] { Candidate("guid-a"), Candidate("guid-b"), Candidate("guid-c") };

        var outcomes = await arbiter.ArbitrateAsync(candidates, Context(), CancellationToken.None);

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(new[] { "guid-a", "guid-b", "guid-c" }, outcomes.Select(o => o.Guid));
    }

    // ---- arb-hwqv: the fail-open catch reports itself -----------------------------------------
    //
    // A distinctive planted title: its absence from a log line means something, where the absence of
    // a generic "Movie.WEB" could be coincidence (CLAUDE.md §4).
    private const string PlantedTitle = "PLANTEDTITLE.Hwq7.Unlikely.To.Appear.By.Chance";

    private static ReleaseCandidate PlantedCandidate(string guid) => new()
    {
        Title = PlantedTitle,
        Guid = guid,
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    [Fact]
    public async Task ArbitrateAsync_TransportFailure_WritesExactlyOneWarning_CarryingTheExceptionType()
    {
        var logger = new RecordingLogger<SyncReleaseArbiter>();
        var arbiter = new SyncReleaseArbiter(new ThrowingOllamaClient(new HttpRequestException("boom")), logger);

        await arbiter.ArbitrateAsync(new[] { PlantedCandidate("guid-1") }, Context(), CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(nameof(HttpRequestException), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArbitrateAsync_TransportFailure_LogsNoReleaseIdentity()
    {
        var logger = new RecordingLogger<SyncReleaseArbiter>();
        var arbiter = new SyncReleaseArbiter(new ThrowingOllamaClient(new HttpRequestException("boom")), logger);

        await arbiter.ArbitrateAsync(new[] { PlantedCandidate("guid-planted") }, Context(), CancellationToken.None);

        // Positive control FIRST: prove the lines exist and carry the text they are supposed to,
        // so the absence assertions below are assertions about a populated set, not a vacuous pass
        // over an empty one.
        Assert.NotEmpty(logger.Entries);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(nameof(HttpRequestException), warning.Message, StringComparison.Ordinal);

        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(PlantedTitle, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("guid-planted", entry.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ArbitrateAsync_ExceedingTheBudget_WritesNoWarning()
    {
        // A budget overrun is a routine outcome on this request path, not an operator-actionable
        // fault, so it must stay off the Warning level however many candidates hit it.
        var logger = new RecordingLogger<SyncReleaseArbiter>();
        var arbiter = new SyncReleaseArbiter(new NeverCompletingOllamaClient(), logger);

        var outcomes = await arbiter.ArbitrateAsync(
            new[] { PlantedCandidate("guid-1") }, Context(TimeSpan.FromMilliseconds(50)), CancellationToken.None);

        Assert.Equal(Verdict.Unknown, Assert.Single(outcomes).Verdict);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ArbitrateAsync_SuccessfulArbitration_WritesNoWarning()
    {
        var logger = new RecordingLogger<SyncReleaseArbiter>();
        var arbiter = new SyncReleaseArbiter(new StaticOllamaClient(new OllamaVerdict("accept", 0.9)), logger);

        await arbiter.ArbitrateAsync(new[] { PlantedCandidate("guid-1") }, Context(), CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ArbitrateAsync_ManyFailingCandidates_StillWritesExactlyOneWarning()
    {
        // The flood-control assertion: N failures in one arbitration produce ONE Warning, not N.
        var logger = new RecordingLogger<SyncReleaseArbiter>();
        var arbiter = new SyncReleaseArbiter(new ThrowingOllamaClient(new HttpRequestException("boom")), logger);
        var candidates = Enumerable.Range(0, 20).Select(i => PlantedCandidate($"guid-{i}")).ToArray();

        var outcomes = await arbiter.ArbitrateAsync(candidates, Context(), CancellationToken.None);

        Assert.Equal(20, outcomes.Count);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("20 of 20", warning.Message, StringComparison.Ordinal);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    /// <summary>
    /// Copied into this assembly on purpose: a test helper shared across test assemblies would make
    /// one assembly's test run depend on another's build.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) { return _entries.ToArray(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Debug must be enabled, or the Debug arm never renders and the absence assertions would
        // pass over a set the production code was never given the chance to populate.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class ThrowingOllamaClient : IOllamaClient
    {
        private readonly Exception _exception;

        public ThrowingOllamaClient(Exception exception) => _exception = exception;

        public Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default) =>
            throw _exception;
    }

    private sealed class StaticOllamaClient : IOllamaClient
    {
        private readonly OllamaVerdict _verdict;

        public StaticOllamaClient(OllamaVerdict verdict) => _verdict = verdict;

        public Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default) =>
            Task.FromResult(_verdict);
    }

    private sealed class NeverCompletingOllamaClient : IOllamaClient
    {
        public async Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
        {
            // Waits on the caller's own linked token so the test terminates via the arbiter's budget
            // timeout rather than truly hanging forever if the budget somehow failed to fire.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }

    // ---- arb-0nw3: the candidate loop is strictly sequential, not fanned out ------------------
    //
    // Records the maximum number of concurrent in-flight ClassifyAsync calls it observed. If the
    // loop in ArbitrateAsync ever switched to Task.WhenAll/Parallel, this would read > 1; a
    // throwaway console project outside the repo confirmed a Task.WhenAll stand-in over the same
    // candidates does read > 1 with this exact fake (see commit body for the 2-line result), so
    // this test is a genuine, non-vacuous positive control for the loop shape.
    private sealed class ConcurrencyTrackingOllamaClient : IOllamaClient
    {
        private int _inFlight;
        private int _maxInFlight;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, current);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
                return new OllamaVerdict("accept", 0.5);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int candidate)
        {
            int initial;
            do
            {
                initial = Volatile.Read(ref target);
                if (candidate <= initial) return;
            }
            while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
        }
    }

    [Fact]
    public async Task ArbitrateAsync_MultipleCandidates_NeverArbitratesMoreThanOneConcurrently()
    {
        var trackingClient = new ConcurrencyTrackingOllamaClient();
        var arbiter = new SyncReleaseArbiter(trackingClient);
        var candidates = Enumerable.Range(0, 4).Select(i => Candidate($"guid-{i}")).ToArray();

        await arbiter.ArbitrateAsync(candidates, Context(), CancellationToken.None);

        Assert.Equal(1, trackingClient.MaxInFlight);
    }
}
