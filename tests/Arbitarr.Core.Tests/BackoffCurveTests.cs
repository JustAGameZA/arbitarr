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
///
/// <para>
/// <b>M3-12 rewrite:</b> the previous version of this file asserted a flat ~5-minute gap between every
/// successive attempt — that was proving the bug (<c>NextProbeAt</c> gated by the fixed
/// <see cref="CircuitBreakerOptions.ProbeInterval"/>), not AC20's intended 5s-to-15min doubling curve.
/// This version asserts only on the doubling/jittered deltas between recorded attempt instants, using a
/// seeded <see cref="Random"/> so jitter is deterministic-but-nonzero and every delay assertion checks a
/// ±20% bound rather than an exact value.
/// </para>
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

    /// <summary>Asserts <paramref name="actual"/> is within ±20% of <paramref name="nominalSeconds"/>.</summary>
    private static void AssertWithinTwentyPercent(TimeSpan actual, double nominalSeconds)
    {
        var lower = TimeSpan.FromSeconds(nominalSeconds * 0.8);
        var upper = TimeSpan.FromSeconds(nominalSeconds * 1.2);
        Assert.True(actual >= lower && actual <= upper,
            $"expected delay within ±20% of {nominalSeconds}s (i.e. [{lower}, {upper}]), but observed {actual}");
    }

    [Fact]
    public void ObservedAttempts_StopOccurring_OnceBreakerOpens()
    {
        // AC20 (a): breaker opens after 3 consecutive failures. Polling every second, the fake source
        // must be actually called exactly 3 times, then none until the first delay elapses -- proven
        // by the attempt log plateauing, not by inspecting ConsecutiveFailures.
        var clock = new FakeTimeProvider(Start);
        var source = new FakeFailingSource(clock);
        var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0 }, new Random(1));

        DriveFailingLoop(breaker, clock, source, TimeSpan.FromSeconds(1), maxAttempts: 3);
        var countAtOpen = source.AttemptTimestamps.Count;

        // Poll well short of the ~5s initial backoff: no further attempt must occur.
        for (var i = 0; i < 3; i++)
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
        // AC20 (b)+(c): drive the breaker through repeated probe failures and assert the recorded
        // attempt-instant deltas follow the doubling curve 5s, 10s, 20s, ... up to the 900s (15min)
        // ceiling, each within +/-20% of nominal (seeded jitter, deterministic but nonzero). Also
        // prove the curve passes through the ~5 minute region (one delay within +/-20% of 300s) --
        // not that it stops there.
        var clock = new FakeTimeProvider(Start);
        var source = new FakeFailingSource(clock);
        var breaker = new SourceCircuitBreaker(clock, CircuitBreakerOptions.Default, new Random(1234));

        // Open the breaker: 3 back-to-back closed-state failures, fast polling.
        DriveFailingLoop(breaker, clock, source, TimeSpan.FromMilliseconds(100), maxAttempts: 3);
        Assert.Equal(3, source.AttemptTimestamps.Count);

        // Each probe's delay is the *previous jittered* delay doubled (and re-jittered), capped at
        // 900s -- not a pure doubling of the original nominal, since jitter compounds step over step.
        // Drive enough probes to reach and hold the ceiling.
        const int probeCount = 10;
        var totalAttempts = 3 + probeCount;

        // Drive one probe attempt per expected delay: advance the clock in fine ticks so the observed
        // gap between attempts is dictated purely by the breaker's own gating logic, not poll cadence.
        DriveFailingLoop(breaker, clock, source, TimeSpan.FromSeconds(1), maxAttempts: totalAttempts);
        Assert.Equal(totalAttempts, source.AttemptTimestamps.Count);

        var observedDelays = new List<TimeSpan>();
        for (var i = 3; i < totalAttempts; i++)
        {
            observedDelays.Add(source.AttemptTimestamps[i] - source.AttemptTimestamps[i - 1]);
        }

        // First delay must be ~5s (AC20's initial backoff).
        AssertWithinTwentyPercent(observedDelays[0], 5);

        // Each subsequent delay must be within +/-20% of double the previous one, until the 900s
        // ceiling is reached, after which it must stay pinned at ~900s. This proves the doubling
        // relationship step-by-step without accumulating jitter drift against a fixed nominal table.
        for (var i = 1; i < observedDelays.Count; i++)
        {
            var doubledPrevious = observedDelays[i - 1] * 2;
            if (doubledPrevious >= TimeSpan.FromMinutes(15))
            {
                AssertWithinTwentyPercent(observedDelays[i], 900);
            }
            else
            {
                AssertWithinTwentyPercent(observedDelays[i], doubledPrevious.TotalSeconds);
            }
        }

        // Curve must actually pass through the ~5-minute (300s) region on its way to the ceiling --
        // proving it traverses that magnitude, not that it lands there exactly (per-step jitter of
        // up to +/-20% compounds across the doubling chain, so the crossing step can land anywhere
        // from half to double the pure-nominal 300s; a band that wide still rules out curves that
        // jump straight from well under a minute to the 15-minute ceiling in a single bound).
        Assert.Contains(observedDelays, d => d >= TimeSpan.FromSeconds(150) && d <= TimeSpan.FromSeconds(600));

        // Ceiling must actually be reached and held for the final probes.
        AssertWithinTwentyPercent(observedDelays[^1], 900);
        AssertWithinTwentyPercent(observedDelays[^2], 900);
    }

    [Fact]
    public void ObservedAttempts_Resume_AfterSuccessResetsCurve()
    {
        // AC20 (d): a successful probe closes the breaker; the very next call is permitted
        // immediately (no lingering backoff wait), and the curve must reset -- 3 fresh consecutive
        // failures are required to reopen, and the very first reopen delay must again be ~5s, not a
        // continuation of whatever backoff had grown to before the success.
        var clock = new FakeTimeProvider(Start);
        var source = new FakeFailingSource(clock);
        var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0 }, new Random(1));

        // Open, then grow the backoff via a couple of failed probes so CurrentBackoff is well past 5s.
        DriveFailingLoop(breaker, clock, source, TimeSpan.FromMilliseconds(100), maxAttempts: 3);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(breaker.CanCall(Source));
        breaker.RecordFailure(Source, source.Attempt()); // probe fails: backoff grows to 10s
        Assert.Equal(TimeSpan.FromSeconds(10), breaker.GetSnapshot(Source).CurrentBackoff);

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(breaker.CanCall(Source)); // Half-Open probe slot

        // This time the probe succeeds.
        breaker.RecordSuccess(Source);
        var snapshotAfterSuccess = breaker.GetSnapshot(Source);
        Assert.Equal(CircuitState.Closed, snapshotAfterSuccess.State);
        Assert.Equal(TimeSpan.Zero, snapshotAfterSuccess.CurrentBackoff);

        // The very next call is permitted immediately -- no delay at all.
        Assert.True(breaker.CanCall(Source));
        breaker.RecordFailure(Source, source.Attempt());
        Assert.Equal(CircuitState.Closed, breaker.GetSnapshot(Source).State);

        // Exactly 2 more back-to-back closed-state failures must be tolerated (no gating) before the
        // breaker reopens -- proving 3 fresh consecutive failures are needed, same as the first open.
        breaker.RecordFailure(Source, source.Attempt());
        Assert.Equal(CircuitState.Closed, breaker.GetSnapshot(Source).State);
        breaker.RecordFailure(Source, source.Attempt());
        Assert.Equal(CircuitState.Open, breaker.GetSnapshot(Source).State);

        // And the curve must have reset: the reopen delay is ~5s again, not a continuation of the 10s
        // (or higher) backoff that was in effect before the successful probe.
        Assert.Equal(TimeSpan.FromSeconds(5), breaker.GetSnapshot(Source).CurrentBackoff);

        var lastClosedAttempt = source.AttemptTimestamps[^1];
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(breaker.CanCall(Source)); // not yet 5s since reopening
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(breaker.CanCall(Source));
        breaker.RecordFailure(Source, source.Attempt());
        var nextProbeAttempt = source.AttemptTimestamps[^1];

        AssertWithinTwentyPercent(nextProbeAttempt - lastClosedAttempt, 5);
    }
}
