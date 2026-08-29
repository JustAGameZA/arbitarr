namespace ArrSearcher.Sources.NzbHydra;

/// <summary>
/// Simple per-instance token-bucket rate limiter used to throttle outbound calls to a single
/// upstream source. Configured via constructor parameters (not hardcoded) so callers can tune
/// it per source instance.
/// </summary>
public sealed class RateLimiter
{
    private readonly int _maxTokens;
    private readonly TimeSpan _refillInterval;
    private readonly object _gate = new();
    private double _availableTokens;
    private DateTimeOffset _lastRefill;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="maxTokens">Maximum number of calls permitted within one <paramref name="refillInterval"/> window (bucket capacity).</param>
    /// <param name="refillInterval">The time window over which the bucket fully refills.</param>
    /// <param name="now">Clock abstraction for testability; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public RateLimiter(int maxTokens, TimeSpan refillInterval, Func<DateTimeOffset>? now = null)
    {
        if (maxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), "maxTokens must be positive.");
        }

        if (refillInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refillInterval), "refillInterval must be positive.");
        }

        _maxTokens = maxTokens;
        _refillInterval = refillInterval;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _availableTokens = maxTokens;
        _lastRefill = _now();
    }

    /// <summary>
    /// Waits (asynchronously) until a token is available, then consumes one. Never throws for
    /// exhausted capacity — callers experience backpressure via delay, not failure.
    /// </summary>
    public async Task WaitForTokenAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                Refill();

                if (_availableTokens >= 1)
                {
                    _availableTokens -= 1;
                    return;
                }

                var tokensNeeded = 1 - _availableTokens;
                var secondsPerToken = _refillInterval.TotalSeconds / _maxTokens;
                delay = TimeSpan.FromSeconds(tokensNeeded * secondsPerToken);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Refill()
    {
        var now = _now();
        var elapsed = now - _lastRefill;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var tokensToAdd = elapsed.TotalSeconds / _refillInterval.TotalSeconds * _maxTokens;
        if (tokensToAdd > 0)
        {
            _availableTokens = Math.Min(_maxTokens, _availableTokens + tokensToAdd);
            _lastRefill = now;
        }
    }
}
