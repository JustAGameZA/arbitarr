using Arbitarr.Core.Arbitration;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;
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
}
