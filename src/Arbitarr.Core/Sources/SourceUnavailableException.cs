namespace Arbitarr.Core.Sources;

/// <summary>
/// Thrown by an <see cref="IUpstreamSource"/> when it refuses to call upstream because its circuit
/// breaker is open. The source is not known to be broken for this request — it is being deliberately
/// rested after earlier failures — so the endpoint layer must answer with a retryable
/// <c>503 Service Unavailable</c>, never a 5xx that reads as an application fault.
/// </summary>
/// <remarks>
/// Before this type existed the open-breaker path threw a bare <see cref="InvalidOperationException"/>,
/// which no endpoint caught: Sonarr's download retries each hit an unhandled 500 while the breaker
/// rested, and each 500 was logged as a Kestrel application crash (2026-09-08, 267 times in ten
/// minutes). A typed exception is what lets the download proxy distinguish "come back later" from
/// "something is wrong".
/// </remarks>
public sealed class SourceUnavailableException : Exception
{
    public SourceUnavailableException(string sourceName)
        : base($"Circuit breaker for source '{sourceName}' is open; refusing to call upstream.")
    {
        SourceName = sourceName;
    }

    public string SourceName { get; }
}
