namespace Arbitarr.Media.Providers;

/// <summary>
/// Outcome of an <see cref="ArrApiProvider"/> fetch, distinguishing the two failure shapes AC-M6
/// requires callers to tell apart, mirroring the <c>XemResult</c>/<c>XemOutcomeKind</c> idiom used by
/// <see cref="XemProvider"/> so both providers in the Q5-D order report degradation the same way.
/// </summary>
public enum ArrApiOutcomeKind
{
    /// <summary>The call succeeded; episode data (possibly empty) was returned.</summary>
    Success,

    /// <summary>
    /// The *arr instance could not be reached: the circuit breaker was open, the HTTP call failed, or
    /// *arr returned a non-success status. This is <see cref="Arbitarr.Core.Identity.MatchProvenanceFlags.SourceUnreachable"/>,
    /// distinct from *arr being reachable but simply having no episode for the requested series/numbers.
    /// </summary>
    Unreachable,
}

/// <summary>
/// Result envelope for <see cref="ArrApiProvider"/> fetches, carrying either a successful payload or
/// the distinct "source unreachable" degraded state.
/// </summary>
/// <typeparam name="T">The payload type on success.</typeparam>
public sealed record ArrApiResult<T>(ArrApiOutcomeKind Kind, T? Value)
{
    public static ArrApiResult<T> Success(T value) => new(ArrApiOutcomeKind.Success, value);

    public static ArrApiResult<T> Unreachable() => new(ArrApiOutcomeKind.Unreachable, default);
}
