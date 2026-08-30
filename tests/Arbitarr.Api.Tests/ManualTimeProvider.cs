namespace Arbitarr.Api.Tests;

/// <summary>Minimal hand-rolled <see cref="TimeProvider"/> with a settable "now", for TTL-expiry tests.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
