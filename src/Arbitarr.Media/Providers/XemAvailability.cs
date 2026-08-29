namespace ArrSearcher.Media.Providers;

/// <summary>
/// Outcome of a <see cref="XemProvider"/> call, distinguishing the two failure shapes that
/// AC-M6 requires callers (the arc-identification/matcher code) to tell apart:
/// <list type="bullet">
/// <item><description><see cref="Unreachable"/> — the XEM service could not be reached or
/// returned a server error (network failure, timeout, non-success/5xx HTTP status). This is a
/// transient "source unreachable" degraded state: the series may well have XEM coverage, we
/// simply could not ask.</description></item>
/// <item><description><see cref="NoCoverage"/> — XEM was reached successfully and affirmatively
/// reported (via <c>havemap</c>) that the series has no XEM mapping at all. This is a stable
/// "no-xem-coverage" degraded state, distinct from an outage, and is negative-cacheable.</description></item>
/// </list>
/// </summary>
public enum XemOutcomeKind
{
    /// <summary>The call succeeded and data is present.</summary>
    Success,

    /// <summary>XEM could not be reached, or responded with a server error. Transient.</summary>
    Unreachable,

    /// <summary>XEM was reached but reports no coverage for this series (<c>havemap</c> is false).</summary>
    NoCoverage,
}

/// <summary>
/// Result envelope for <see cref="XemProvider"/> calls, carrying either a successful payload or
/// one of the two distinct degraded states AC-M6 requires to be distinguishable.
/// </summary>
/// <typeparam name="T">The payload type on success.</typeparam>
public sealed record XemResult<T>(XemOutcomeKind Kind, T? Value)
{
    public static XemResult<T> Success(T value) => new(XemOutcomeKind.Success, value);

    public static XemResult<T> Unreachable() => new(XemOutcomeKind.Unreachable, default);

    public static XemResult<T> NoCoverage() => new(XemOutcomeKind.NoCoverage, default);
}
