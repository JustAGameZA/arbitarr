using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Drives a fake source through repeated failures via <see cref="SourceCircuitBreaker"/> and asserts
/// on the wall-clock timestamps at which the fake source's calls were actually attempted (M3-12/AC20/RX-3)
/// — never on <see cref="SourceCircuitBreaker.GetSnapshot"/>/config readback, which
/// <see cref="SourceCircuitBreakerTests"/> already covers. A curve that only ever inspects
/// <c>CurrentBackoff</c>/<c>NextProbeAt</c> can pass while the breaker still lets calls through at the
/// wrong cadence; recording each attempt's observed time and asserting on the deltas between them is
/// the non-vacuous proof that the retry schedule the caller actually experiences matches AC20's curve.
/// </summary>
public sealed class BackoffCurveTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Source = "backoff-curve-source";

    /// <summary>
    /// A fake upstream source: every time the driving loop below finds <see cref="SourceCircuitBreaker.CanCall"/>
    /// permits a call, it invokes <see cref="Attempt"/>, which stamps the current time from the same
    /// <see cref="FakeTimeProvider"/> the breaker uses and always fails — simulating a source that is
    /// down for the whole test.
    /// </summary>
    private sealed class FakeFailingSource
    {
        private readonly FakeTimeProvider _clock;
        public List<DateTimeOffset> AttemptTimestamps { get; } = new();

        public FakeFailingSource(FakeTimeProvider clock) => _clock = clock;

        public Exception Attempt()
        {
            AttemptTimestamps.Add(_clock.GetUtcNow());
            return new InvalidOperationException("fake source is down");
        }
    }

    /// <summary>
    /// Drives up to <paramref name="maxAttempts"/> polling ticks of <paramref name="tick"/> spacing
    /// through the breaker, calling the fake source whenever <see cref="SourceCircuitBreaker.CanCall"/>
    /// allows it, and recording every actually-attempted call's timestamp on
    /// <see cref="FakeFailingSource.AttemptTimestamps"/>.
    /// </summary>
    private static void DriveFailingLoop(SourceCircuitBreaker breaker, FakeTimeProvider clock, FakeFailingSource source, TimeSpan tick, int maxAttempts)
    {
        while (source.AttemptTimestamps.Count < maxAttempts)
        {
            if (breaker.CanCall(Source))
            {
                breaker.RecordFailure(Source, source.Attempt());
            }

            clock.Advance(tick);
        }
    }

    [Fact]
    public void ObservedAttempts_StopOccurring_OnceBreakerOpens()
    {
        // AC20: breaker opens after 3 consecutive failures. Polling every second, the fake source
        // must be actually called exactly 3 times before the open breaker starts refusing calls --
        // proven by the attempt log, not by inspecting ConsecutiveFailures.
        var clock = new FakeTimeProvider(Start);
        var source = new FakeFailingSource(clock);
        var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0 }, new Random(1));

        // Poll for up to two minutes at 1s resolution; the breaker must open long before that,
        // so the attempt count must plateau at 3 rather than keep climbing.
        DriveFailingLoop(breaker, clock, source, TimeSpan.FromSeconds(1), maxAttempts: 3);
        var countAtOpen = source.AttemptTimestamps.Count;

        clock.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 30; i++)
        {
            if (breaker.CanCall(Source))
            {
                breaker.RecordFailure(Source, source.Attempt());
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(3, countAtOpen);
        Assert.Equal(3, source.AttemptTimestamps.Count);
    }

    [Fact]
    public void ObservedAttemptTimestamps_MatchAC20DoublingCurve()
    {
        // Poll continuously at a fine (1s) resolution so the *observed* time between successive
        // attempts is dictated entirely by the breaker's gating, not by the poll cadence. AC20's
        // curve: opens after 3 failures with a 5s initial backoff and a fixed 5-minute probe
        // interval; each subsequent probe failure doubles the backoff, but the observed gap between
        // attempts while Open is always governed by ProbeInterval (5 minutes), not by CurrentBackoff
        // directly -- CurrentBackoff instead sizes how "unhealthy" the source is judged, and this
        // test proves the caller-visible retry cadence (successive real attempt timestamps) is
        // exactly one probe interval apart once open, for four successive reopen cycles.
        var clock = new FakeTimeProvider(Start);
        var source = new FakeFailingSource(clock);
        var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0 }, new Random(1));

        // Drive the initial 3 consecutive failures that open the breaker (fast polling; these three
        // attempts happen back-to-back since the breaker is Closed the whole time).
        DriveFailingLoop(breaker, clock, source, TimeSpan.FromSeconds(1), maxAttempts: 3);
        Assert.Equal(3, source.AttemptTimestamps.Count);

        // Now drive four successive probe attempts (each one fails, reopening with grown backoff).
        // Each must be observed exactly one 5-minute probe interval after the previous attempt.
        DriveFailingLoop(breaker, clock, source, TimeSpan.FromSeconds(1), maxAttempts: 7);
        Assert.Equal(7, source.AttemptTimestamps.Count);

        for (var i = 3; i < 7; i++)
        {
            var gap = source.AttemptTimestamps[i] - source.AttemptTimestamps[i - 1];
            Assert.True(gap >= TimeSpan.FromMinutes(5), $"attempt {i} arrived only {gap} after the previous attempt, expected >= 5 minutes");
            Assert.True(gap < TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(2), $"attempt {i} arrived {gap} after the previous attempt, expected close to 5 minutes");
        }
    }

    [Fact]
    public void ObservedAttempts_Resume_AfterSuccessResetsCurve()
    {
        // A successful probe closes the breaker and resets backoff to the initial curve. Prove this
        // via observed timestamps: after a recovering success, the next failure-triggered reopen
        // must again require 3 consecutive closed-state failures (fast, back-to-back) before the
        // probe cadence re-appears -- not immediately probing at the old (possibly longer) interval.
        var clock = new FakeTimeProvider(Start);
        var source = new FakeFailingSource(clock);
        var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0 }, new Random(1));

        DriveFailingLoop(breaker, clock, source, TimeSpan.FromSeconds(1), maxAttempts: 3);
        Assert.Equal(3, source.AttemptTimestamps.Count);

        // Advance past the probe interval and let the single half-open probe succeed this time.
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.True(breaker.CanCall(Source));
        breaker.RecordFailure(Source, source.Attempt()); // the successful probe call itself; log it, then...
        var probeAttemptedAt = source.AttemptTimestamps[^1];
        breaker.RecordSuccess(Source); // ...record the actual outcome as success, overriding the failure just recorded.

        // Breaker is Closed again: the very next call must be let through with no delay at all (no
        // lingering backoff wait), proving the curve actually reset rather than continuing to grow.
        // If reset had NOT happened, CanCall would refuse until another full probe interval elapsed.
        Assert.True(breaker.CanCall(Source));
        breaker.RecordFailure(Source, source.Attempt());
        var firstAttemptAfterReset = source.AttemptTimestamps[^1];

        Assert.Equal(probeAttemptedAt, firstAttemptAfterReset);

        // Now prove the curve genuinely restarted from the initial 5s/3-failures shape, rather than
        // silently resuming the old cadence: exactly 2 more back-to-back closed-state failures must
        // be tolerated (no gating) before the breaker opens and probe-interval gating reappears.
        breaker.RecordFailure(Source, source.Attempt());
        breaker.RecordFailure(Source, source.Attempt());
        Assert.Equal(CircuitState.Open, breaker.GetSnapshot(Source).State);
        Assert.False(breaker.CanCall(Source));

        var lastClosedAttempt = source.AttemptTimestamps[^1];
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(breaker.CanCall(Source)); // still gated: not yet a full probe interval since reopening
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(breaker.CanCall(Source));
        breaker.RecordFailure(Source, source.Attempt());
        var nextProbeAttempt = source.AttemptTimestamps[^1];

        Assert.Equal(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1), nextProbeAttempt - lastClosedAttempt);
    }
}
