namespace Arbitarr.Core.Sources;

/// <summary>
/// Thrown by an <see cref="IUpstreamSource"/> implementation when a per-source or per-search
/// request/rate limit has been reached (e.g. an upstream rate-limit response, or the adapter's
/// own <c>MaxUpstreamCallsPerSearch</c> ceiling). Callers at the endpoint layer must translate
/// this into the Torznab/Newznab rate-limit error element (error code 429/limit-specific) rather
/// than surfacing it as an unhandled 5xx.
/// </summary>
public sealed class RequestLimitReachedException : Exception
{
    public RequestLimitReachedException()
        : base("The upstream source's request limit has been reached.")
    {
    }

    public RequestLimitReachedException(string message)
        : base(message)
    {
    }

    public RequestLimitReachedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
