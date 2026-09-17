using System.Net;
using System.Net.Sockets;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Validates AC20's backoff curve (docs/step0-measurements.md §5 and "Circuit breaker constants")
/// against a genuine test double: a <see cref="FakeTimeProvider"/> whose clock is explicitly
/// advanced across state transitions, and a source that is actually driven through repeated
/// simulated failures/successes via <see cref="SourceCircuitBreaker.RecordFailure"/> /
/// <see cref="SourceCircuitBreaker.RecordSuccess"/> — never just asserted on internal state without
/// driving real calls through it, per team-plan's non-vacuous proof bar.
/// </summary>
public sealed class SourceCircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    private const string Source = "test-source";

    private static (SourceCircuitBreaker Breaker, FakeTimeProvider Clock) Create(CircuitBreakerOptions? options = null)
    {
        var clock = new FakeTimeProvider(Start);
        // Fix jitter to zero for deterministic backoff-value assertions; jitter itself is
        // exercised separately in RecordFailure_AppliesJitter_WithinBounds.
        var breaker = new SourceCircuitBreaker(clock, options ?? new CircuitBreakerOptions { JitterFraction = 0 }, new Random(1));
        return (breaker, clock);
    }

    [Fact]
    public void CanCall_IsTrue_Initially()
    {
        var (breaker, _) = Create();
        Assert.True(breaker.CanCall(Source));
    }

    [Fact]
    public void StaysClosed_ForFirstTwoFailures()
    {
        var (breaker, _) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("failure 1"));
        Assert.Equal(CircuitState.Closed, breaker.GetSnapshot(Source).State);
        Assert.True(breaker.CanCall(Source));

        breaker.RecordFailure(Source, new InvalidOperationException("failure 2"));
        Assert.Equal(CircuitState.Closed, breaker.GetSnapshot(Source).State);
        Assert.True(breaker.CanCall(Source));
    }

    [Fact]
    public void Opens_OnThirdConsecutiveFailure()
    {
        var (breaker, _) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("failure 1"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 2"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 3"));

        var snapshot = breaker.GetSnapshot(Source);
        Assert.Equal(CircuitState.Open, snapshot.State);
        Assert.Equal(3, snapshot.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromSeconds(5), snapshot.CurrentBackoff);
    }

    [Fact]
    public void RefusesCalls_WhileOpen_BeforeProbeIntervalElapses()
    {
        var (breaker, clock) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("failure 1"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 2"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 3"));

        Assert.False(breaker.CanCall(Source));

        // Advance most, but not all, of the 5s initial backoff (M3-12: the gate is CurrentBackoff,
        // not the fixed ProbeInterval).
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(breaker.CanCall(Source));
        Assert.Equal(CircuitState.Open, breaker.GetSnapshot(Source).State);
    }

    [Fact]
    public void AllowsExactlyOneProbeCall_OnceHalfOpen()
    {
        var (breaker, clock) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("failure 1"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 2"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 3"));

        // Advance exactly past the 5s initial backoff (M3-12: gated by CurrentBackoff).
        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(1));

        // First call after the probe interval elapses transitions to Half-Open and is permitted.
        Assert.True(breaker.CanCall(Source));
        Assert.Equal(CircuitState.HalfOpen, breaker.GetSnapshot(Source).State);

        // A second call before the outstanding probe is resolved must be refused.
        Assert.False(breaker.CanCall(Source));
    }

    [Fact]
    public void Closes_WhenHalfOpenProbeSucceeds()
    {
        var (breaker, clock) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("failure 1"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 2"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 3"));

        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(1));
        Assert.True(breaker.CanCall(Source)); // transitions to Half-Open, consumes the probe slot

        breaker.RecordSuccess(Source);

        var snapshot = breaker.GetSnapshot(Source);
        Assert.Equal(CircuitState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(TimeSpan.Zero, snapshot.CurrentBackoff);
        Assert.True(breaker.CanCall(Source));
    }

    [Fact]
    public void Reopens_WithGrownBackoff_WhenHalfOpenProbeFails()
    {
        var (breaker, clock) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("failure 1"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 2"));
        breaker.RecordFailure(Source, new InvalidOperationException("failure 3"));

        var firstBackoff = breaker.GetSnapshot(Source).CurrentBackoff;
        Assert.Equal(TimeSpan.FromSeconds(5), firstBackoff);

        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromSeconds(1));
        Assert.True(breaker.CanCall(Source)); // Half-Open probe call

        breaker.RecordFailure(Source, new InvalidOperationException("probe failed"));

        var snapshot = breaker.GetSnapshot(Source);
        Assert.Equal(CircuitState.Open, snapshot.State);
        // Backoff doubles from the prior 5s -> 10s (jitter fixed to 0 for this test).
        Assert.Equal(TimeSpan.FromSeconds(10), snapshot.CurrentBackoff);
        Assert.False(breaker.CanCall(Source));

        // Confirm the new (doubled) probe window is honored: not yet elapsed at old cadence...
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(breaker.CanCall(Source));

        // ...but elapses after the full new 10s backoff from the second open (M3-12: gated by
        // CurrentBackoff, not a fixed probe interval).
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(breaker.CanCall(Source));
        Assert.Equal(CircuitState.HalfOpen, breaker.GetSnapshot(Source).State);
    }

    [Fact]
    public void FullCycle_RepeatedFailures_GrowBackoffUpToCeiling()
    {
        var (breaker, clock) = Create();

        // Open the breaker.
        breaker.RecordFailure(Source, new InvalidOperationException("f1"));
        breaker.RecordFailure(Source, new InvalidOperationException("f2"));
        breaker.RecordFailure(Source, new InvalidOperationException("f3"));
        Assert.Equal(TimeSpan.FromSeconds(5), breaker.GetSnapshot(Source).CurrentBackoff);

        var expectedBackoffs = new[]
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(80),
        };

        foreach (var expected in expectedBackoffs)
        {
            // M3-12: the gate is the current (pre-this-failure) CurrentBackoff, not a fixed interval.
            var currentBackoff = breaker.GetSnapshot(Source).CurrentBackoff;
            clock.Advance(currentBackoff + TimeSpan.FromSeconds(1));
            Assert.True(breaker.CanCall(Source));
            Assert.Equal(CircuitState.HalfOpen, breaker.GetSnapshot(Source).State);

            breaker.RecordFailure(Source, new InvalidOperationException("probe failed again"));
            Assert.Equal(expected, breaker.GetSnapshot(Source).CurrentBackoff);
        }
    }

    [Fact]
    public void Backoff_IsCapped_AtFifteenMinutes()
    {
        var (breaker, clock) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("f1"));
        breaker.RecordFailure(Source, new InvalidOperationException("f2"));
        breaker.RecordFailure(Source, new InvalidOperationException("f3"));

        // 5s -> 10 -> 20 -> 40 -> 80 -> 160 -> 320 -> 640 -> 900 (capped, since 1280 > 900).
        // Drive enough open/probe/fail cycles to exceed the 15-minute (900s) ceiling.
        for (var i = 0; i < 10; i++)
        {
            var snapshot = breaker.GetSnapshot(Source);
            var probeWait = snapshot.NextProbeAt!.Value - clock.GetUtcNow();
            clock.Advance(probeWait + TimeSpan.FromSeconds(1));
            Assert.True(breaker.CanCall(Source));
            breaker.RecordFailure(Source, new InvalidOperationException($"probe failed #{i}"));
        }

        Assert.True(breaker.GetSnapshot(Source).CurrentBackoff <= TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.FromMinutes(15), breaker.GetSnapshot(Source).CurrentBackoff);
    }

    [Fact]
    public void RecordFailure_AppliesJitter_WithinTwentyPercentBounds()
    {
        var clock = new FakeTimeProvider(Start);
        var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0.2 }, new Random(42));

        breaker.RecordFailure(Source, new InvalidOperationException("f1"));
        breaker.RecordFailure(Source, new InvalidOperationException("f2"));
        breaker.RecordFailure(Source, new InvalidOperationException("f3"));

        var backoff = breaker.GetSnapshot(Source).CurrentBackoff;
        var lowerBound = TimeSpan.FromSeconds(5 * 0.8);
        var upperBound = TimeSpan.FromSeconds(5 * 1.2);

        Assert.InRange(backoff, lowerBound, upperBound);
    }

    [Fact]
    public void IndependentSources_DoNotAffectEachOther()
    {
        var (breaker, _) = Create();

        breaker.RecordFailure("source-a", new InvalidOperationException("f1"));
        breaker.RecordFailure("source-a", new InvalidOperationException("f2"));
        breaker.RecordFailure("source-a", new InvalidOperationException("f3"));

        Assert.Equal(CircuitState.Open, breaker.GetSnapshot("source-a").State);
        Assert.Equal(CircuitState.Closed, breaker.GetSnapshot("source-b").State);
        Assert.True(breaker.CanCall("source-b"));
    }

    [Fact]
    public void RecordFailure_SanitizesLastError_NeverSurfacesRawExceptionMessage()
    {
        // A raw HttpRequestException message from a DNS/connect failure routinely embeds the
        // upstream host:port (e.g. "No such host is known. (example.invalid:5076)"), which would
        // leak LAN topology through LastError into both persistence (SourceHealthRecord) and the
        // unauthenticated /api/status dashboard. RecordFailure must never store that raw text.
        var (breaker, _) = Create();
        const string leakyMessage = "No such host is known. (example.invalid:5076)";

        breaker.RecordFailure(Source, new HttpRequestException(leakyMessage, null, System.Net.HttpStatusCode.ServiceUnavailable));

        var lastError = breaker.GetSnapshot(Source).LastError;
        Assert.NotNull(lastError);
        Assert.DoesNotContain("example.invalid", lastError);
        Assert.DoesNotContain(leakyMessage, lastError);
        Assert.Equal("HttpRequestException (503 ServiceUnavailable)", lastError);
    }

    [Fact]
    public void RecordFailure_SanitizesLastError_ToExceptionTypeName_WhenNoStatusCode()
    {
        // Exceptions without an HTTP status code (e.g. a raw socket/DNS failure surfaced as
        // InvalidOperationException, or any non-HttpRequestException) fall back to just the
        // exception type name — still never the raw, potentially host-bearing message text.
        var (breaker, _) = Create();

        breaker.RecordFailure(Source, new InvalidOperationException("connect failed to example.invalid:5076"));

        var lastError = breaker.GetSnapshot(Source).LastError;
        Assert.Equal(nameof(InvalidOperationException), lastError);
    }

    [Fact]
    public void Seed_RestoresPersistedState()
    {
        var (breaker, clock) = Create();
        var persisted = new CircuitBreakerSnapshot(
            State: CircuitState.Open,
            ConsecutiveFailures: 5,
            BaseBackoff: TimeSpan.FromSeconds(20),
            CurrentBackoff: TimeSpan.FromSeconds(20),
            LastFailureAt: Start,
            LastSuccessAt: null,
            LastError: "restored from persistence",
            LastOutcome: SourceStatusOutcome.UpstreamError,
            LastUpstreamStatusCode: 503,
            NextProbeAt: Start + TimeSpan.FromMinutes(5));

        breaker.Seed(Source, persisted);

        Assert.Equal(CircuitState.Open, breaker.GetSnapshot(Source).State);
        Assert.Equal(TimeSpan.FromSeconds(20), breaker.GetSnapshot(Source).BaseBackoff);
        // arb-mhd2: the seeded outcome and status code survive Seed like every other field. A
        // restart rehydrating the breaker must not report "nothing failed" for an Open breaker.
        Assert.Equal(SourceStatusOutcome.UpstreamError, breaker.GetSnapshot(Source).LastOutcome);
        Assert.Equal(503, breaker.GetSnapshot(Source).LastUpstreamStatusCode);
        Assert.False(breaker.CanCall(Source));

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.True(breaker.CanCall(Source));
    }

    /// <summary>
    /// arb-mhd2: one row per exception family the breaker can be handed, asserting the outcome the
    /// writer records for it. This is the whole point of classifying AT THE WRITER — the exception
    /// is in hand here and nowhere later, so a projection-time guess from the sanitized text is
    /// never needed and is explicitly not how this works.
    ///
    /// <para>The upstream status code travels as a separate nullable int for the same reason: it is
    /// read structurally off the exception, never parsed back out of the error string.</para>
    /// </summary>
    public static TheoryData<Exception, SourceStatusOutcome, int?> FailureFamilies() => new()
    {
        // An answer from upstream that carried an auth refusal. Auth is checked before the general
        // status arm, so a 401/403 never degrades into the generic upstream-error bucket.
        { new HttpRequestException("denied", null, HttpStatusCode.Unauthorized), SourceStatusOutcome.AuthRejected, 401 },
        { new HttpRequestException("denied", null, HttpStatusCode.Forbidden), SourceStatusOutcome.AuthRejected, 403 },
        // Any other status: we reached something and it answered badly.
        { new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway), SourceStatusOutcome.UpstreamError, 502 },
        { new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable), SourceStatusOutcome.UpstreamError, 503 },
        // No status at all: nothing answered, so this is a reachability failure, not an upstream one.
        { new HttpRequestException("no such host"), SourceStatusOutcome.Unreachable, null },
        { new SocketException(10061), SourceStatusOutcome.Unreachable, null },
        // Timeouts, both shapes HttpClient produces.
        { new TaskCanceledException("timed out"), SourceStatusOutcome.Timeout, null },
        { new TimeoutException("timed out"), SourceStatusOutcome.Timeout, null },
        // Anything else is ours, not theirs.
        { new InvalidOperationException("no such table"), SourceStatusOutcome.InternalError, null },
    };

    [Theory]
    [MemberData(nameof(FailureFamilies))]
    public void RecordFailure_RecordsTheClosedOutcomeForEachExceptionFamily(
        Exception ex,
        SourceStatusOutcome expectedOutcome,
        int? expectedStatusCode)
    {
        var (breaker, _) = Create();

        breaker.RecordFailure(Source, ex);

        var snapshot = breaker.GetSnapshot(Source);
        Assert.Equal(expectedOutcome, snapshot.LastOutcome);
        Assert.Equal(expectedStatusCode, snapshot.LastUpstreamStatusCode);

        // The outcome and the text describe ONE failure and are written together. A path that set
        // only one would publish an outcome contradicting the admin-gated detail.
        Assert.NotNull(snapshot.LastError);

        // Unknown is the projection's value for a stored name it cannot interpret. No live writer
        // can produce it, which is what makes "unknown" mean "recorded before the column existed"
        // rather than "some failure we decided not to name".
        Assert.NotEqual(SourceStatusOutcome.Unknown, snapshot.LastOutcome);
    }

    /// <summary>
    /// arb-mhd2: <c>Unknown</c> is unreachable from a live writer, asserted over the whole family
    /// set at once rather than only inside the per-row theory — a theory row proves one input does
    /// not produce it; this proves none of them does, which is the claim the enum's doc makes.
    /// </summary>
    [Fact]
    public void RecordFailure_NeverRecordsUnknown_ForAnyExceptionFamily()
    {
        var families = FailureFamilies().ToList();
        var recorded = new List<SourceStatusOutcome>();

        foreach (var row in families)
        {
            var (breaker, _) = Create();
            breaker.RecordFailure(Source, (Exception)row[0]!);
            recorded.Add(breaker.GetSnapshot(Source).LastOutcome);
        }

        // Positive control: the sweep actually ran over every family, so the absence below is not
        // an assertion over an empty list.
        Assert.Equal(families.Count, recorded.Count);
        Assert.NotEmpty(recorded);
        Assert.DoesNotContain(SourceStatusOutcome.Unknown, recorded);

        // And none of them left the field at its default either: every family names its failure.
        Assert.DoesNotContain(SourceStatusOutcome.None, recorded);
    }

    /// <summary>
    /// arb-mhd2: a success clears the outcome with the message. An Open breaker that recovers must
    /// not keep publishing the reason it used to be failing for.
    /// </summary>
    [Fact]
    public void RecordSuccess_ClearsTheOutcomeAndTheUpstreamStatusCode()
    {
        var (breaker, _) = Create();
        breaker.RecordFailure(Source, new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway));

        // Positive control: the outcome really was set, so its clearing below is a real transition.
        Assert.Equal(SourceStatusOutcome.UpstreamError, breaker.GetSnapshot(Source).LastOutcome);
        Assert.Equal(502, breaker.GetSnapshot(Source).LastUpstreamStatusCode);

        breaker.RecordSuccess(Source);

        var snapshot = breaker.GetSnapshot(Source);
        Assert.Equal(SourceStatusOutcome.None, snapshot.LastOutcome);
        Assert.Null(snapshot.LastUpstreamStatusCode);
        Assert.Null(snapshot.LastError);
    }
}
