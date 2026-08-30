using Arbitarr.Core.Diagnostics;
using System.Collections.Concurrent;

namespace Arbitarr.Core.Sources.CircuitBreaker;

/// <summary>
/// Per-source circuit breaker implementing AC20's curve as a pure, testable state machine.
///
/// <para>
/// States: Closed (healthy) -&gt; Open (after <see cref="CircuitBreakerOptions.ConsecutiveFailuresToOpen"/>
/// consecutive failures) -&gt; Half-Open (once the current jittered, doubling
/// <see cref="CircuitBreakerSnapshot.CurrentBackoff"/> elapses since the breaker most recently
/// opened) -&gt; Closed (after <see cref="CircuitBreakerOptions.SuccessesToClose"/> success(es) while
/// Half-Open) or back to Open (any failure while Half-Open, with the backoff doubling again, capped
/// at <see cref="CircuitBreakerOptions.MaxBackoff"/>).
/// </para>
///
/// <para>
/// <b>M3-12 correction:</b> the open-duration gate is <c>CurrentBackoff</c> itself, not a separate
/// fixed <see cref="CircuitBreakerOptions.ProbeInterval"/> — a prior version set
/// <c>NextProbeAt = now + ProbeInterval</c> unconditionally, which left every probe exactly 5
/// minutes apart regardless of <c>CurrentBackoff</c>'s doubling, so AC20's 5s-to-15min curve was
/// computed but never actually gated a caller's retry cadence. <c>ProbeInterval</c> is kept only as
/// a legacy config knob (see its own doc comment) and no longer participates in gating.
/// </para>
///
/// <para>
/// Deliberately dependency-free (no SQLite/EF/HTTP): time comes from an injected
/// <see cref="TimeProvider"/> rather than <see cref="DateTimeOffset.UtcNow"/>, so tests can drive
/// state transitions deterministically without sleeping for real minutes. Persistence (writing
/// state into <c>SourceHealthRecord</c> rows) is handled by a separate thin adapter in
/// Arbitarr.Data; this class knows nothing about that entity or SQLite.
/// </para>
/// </summary>
public sealed class SourceCircuitBreaker
{
    private readonly TimeProvider _timeProvider;
    private readonly CircuitBreakerOptions _options;
    private readonly ConcurrentDictionary<string, CircuitBreakerSnapshot> _bySource = new(StringComparer.Ordinal);
    private readonly Random _jitterRandom;

    public SourceCircuitBreaker(TimeProvider timeProvider, CircuitBreakerOptions? options = null, Random? jitterRandom = null)
    {
        _timeProvider = timeProvider;
        _options = options ?? CircuitBreakerOptions.Default;
        _jitterRandom = jitterRandom ?? Random.Shared;
    }

    /// <summary>
    /// Returns whether a call to <paramref name="sourceName"/> is currently permitted.
    /// Closed: always true. Open: true only once <see cref="CircuitBreakerSnapshot.NextProbeAt"/>
    /// has elapsed, at which point the source transitions to Half-Open and exactly one call is let
    /// through as a probe. Half-Open: false for any call after the single probe has been dispatched
    /// (callers should call <see cref="RecordSuccess"/>/<see cref="RecordFailure"/> promptly on the
    /// call this permitted, before asking again).
    /// </summary>
    public bool CanCall(string sourceName)
    {
        var snapshot = GetSnapshot(sourceName);
        var now = _timeProvider.GetUtcNow();

        switch (snapshot.State)
        {
            case CircuitState.Closed:
                return true;

            case CircuitState.HalfOpen:
                // A probe is already in flight (RecordSuccess/RecordFailure not yet called for it).
                return false;

            case CircuitState.Open:
                if (snapshot.NextProbeAt is { } nextProbeAt && now >= nextProbeAt)
                {
                    // Probe interval elapsed: transition to Half-Open and allow exactly this one call.
                    _bySource[sourceName] = snapshot with { State = CircuitState.HalfOpen };
                    return true;
                }

                return false;

            default:
                throw new InvalidOperationException($"Unknown circuit breaker state: {snapshot.State}");
        }
    }

    /// <summary>
    /// Records a successful call. In Closed state this simply resets the failure count. In
    /// Half-Open state (the probe succeeded) this closes the breaker per
    /// <see cref="CircuitBreakerOptions.SuccessesToClose"/> (AC20 default: 1), resetting backoff.
    /// </summary>
    public void RecordSuccess(string sourceName)
    {
        var now = _timeProvider.GetUtcNow();
        _bySource[sourceName] = CircuitBreakerSnapshot.Initial with
        {
            LastSuccessAt = now,
        };
    }

    /// <summary>
    /// Records a failed call. In Closed state, increments the consecutive-failure count and opens
    /// the breaker once <see cref="CircuitBreakerOptions.ConsecutiveFailuresToOpen"/> is reached. In
    /// Half-Open state (the probe failed), reopens the breaker and grows the backoff curve
    /// (doubling, capped at <see cref="CircuitBreakerOptions.MaxBackoff"/>, ±<see cref="CircuitBreakerOptions.JitterFraction"/> jitter).
    /// </summary>
    public void RecordFailure(string sourceName, Exception ex)
    {
        var snapshot = GetSnapshot(sourceName);
        var now = _timeProvider.GetUtcNow();
        var errorMessage = DescribeSanitized(ex);

        switch (snapshot.State)
        {
            case CircuitState.Closed:
            {
                var failures = snapshot.ConsecutiveFailures + 1;
                if (failures >= _options.ConsecutiveFailuresToOpen)
                {
                    var baseBackoff = _options.InitialBackoff;
                    var backoff = ApplyJitter(baseBackoff);
                    _bySource[sourceName] = snapshot with
                    {
                        State = CircuitState.Open,
                        ConsecutiveFailures = failures,
                        BaseBackoff = baseBackoff,
                        CurrentBackoff = backoff,
                        LastFailureAt = now,
                        LastError = errorMessage,
                        NextProbeAt = now + backoff,
                    };
                }
                else
                {
                    _bySource[sourceName] = snapshot with
                    {
                        ConsecutiveFailures = failures,
                        LastFailureAt = now,
                        LastError = errorMessage,
                    };
                }

                break;
            }

            case CircuitState.HalfOpen:
            {
                // Probe failed: reopen and grow the backoff curve (doubling the pre-jitter base,
                // capped, then re-jittered). Doubling snapshot.BaseBackoff (not CurrentBackoff,
                // which already carries jitter) keeps jitter from compounding multiplicatively
                // across successive steps.
                var nextBaseBackoff = DoubleAndCap(snapshot.BaseBackoff);
                var jittered = ApplyJitter(nextBaseBackoff);
                _bySource[sourceName] = snapshot with
                {
                    State = CircuitState.Open,
                    ConsecutiveFailures = snapshot.ConsecutiveFailures + 1,
                    BaseBackoff = nextBaseBackoff,
                    CurrentBackoff = jittered,
                    LastFailureAt = now,
                    LastError = errorMessage,
                    NextProbeAt = now + jittered,
                };
                break;
            }

            case CircuitState.Open:
                // A failure recorded while already Open (e.g. a stale in-flight call) does not
                // extend the current probe window; it just updates the last-seen error.
                _bySource[sourceName] = snapshot with
                {
                    LastFailureAt = now,
                    LastError = errorMessage,
                };
                break;

            default:
                throw new InvalidOperationException($"Unknown circuit breaker state: {snapshot.State}");
        }
    }

    /// <summary>Returns the current snapshot for a source, or <see cref="CircuitBreakerSnapshot.Initial"/> if never observed.</summary>
    public CircuitBreakerSnapshot GetSnapshot(string sourceName)
        => _bySource.TryGetValue(sourceName, out var snapshot) ? snapshot : CircuitBreakerSnapshot.Initial;

    /// <summary>
    /// Seeds/overwrites the in-memory state for a source, e.g. from a persisted
    /// <c>SourceHealthRecord</c> row on startup so breaker state survives restarts. Persistence is
    /// the caller's responsibility (see Arbitarr.Data's adapter) — this method only affects the
    /// in-memory state machine.
    /// </summary>
    public void Seed(string sourceName, CircuitBreakerSnapshot snapshot)
        => _bySource[sourceName] = snapshot;

    /// <summary>
    /// Reduces an exception to a topology-safe description: exception type name, plus the HTTP
    /// status code when the exception carries one. <see cref="Exception.Message"/> is never
    /// surfaced here — for an <see cref="HttpRequestException"/> raised by a DNS/connect failure
    /// or <c>EnsureSuccessStatusCode</c>, the message text routinely embeds the upstream host
    /// (e.g. "No such host is known. (host:5076)"), which would otherwise leak LAN topology
    /// through <see cref="CircuitBreakerSnapshot.LastError"/> into both persistence
    /// (<c>SourceHealthRecord</c>) and the unauthenticated <c>/api/status</c> dashboard.
    /// </summary>
    private static string DescribeSanitized(Exception ex) => SanitizedErrorDescription.Describe(ex);

    private TimeSpan DoubleAndCap(TimeSpan currentBackoff)
    {
        var baseBackoff = currentBackoff <= TimeSpan.Zero ? _options.InitialBackoff : currentBackoff;
        var doubled = baseBackoff * 2;
        return doubled > _options.MaxBackoff ? _options.MaxBackoff : doubled;
    }

    private TimeSpan ApplyJitter(TimeSpan backoff)
    {
        if (_options.JitterFraction <= 0)
        {
            return backoff;
        }

        // Uniform in [-JitterFraction, +JitterFraction], applied multiplicatively.
        var jitter = (_jitterRandom.NextDouble() * 2 - 1) * _options.JitterFraction;
        var jittered = backoff * (1 + jitter);
        return jittered < TimeSpan.Zero ? TimeSpan.Zero : jittered;
    }
}
