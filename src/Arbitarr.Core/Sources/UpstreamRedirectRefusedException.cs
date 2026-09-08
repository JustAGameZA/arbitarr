namespace Arbitarr.Core.Sources;

/// <summary>
/// Thrown by an <see cref="IUpstreamSource"/> when the upstream answered a download request with
/// a redirect instead of the payload. Arbitarr never follows redirects on the download path
/// (SEC-M1: the target is an arbitrary host, not the pinned upstream origin), so this is refused —
/// but it is refused as a <b>configuration</b> answer, not an upstream failure.
/// </summary>
/// <remarks>
/// <para>
/// NZBHydra2 sends exactly this when its <c>Downloading &gt; NZB access type</c> is set to
/// <c>Redirect to indexer</c> rather than <c>Proxy</c>: every <c>/getnzb/api/…</c> call is a 302
/// to the indexer's own NZB URL. The upstream is healthy and answering promptly, and it will give
/// the same answer to every download until the operator changes that setting.
/// </para>
/// <para>
/// That is why a redirect must <b>not</b> be recorded on the circuit breaker. On 2026-09-08 three
/// redirected downloads in a row opened the NZBHydra2 breaker; Sonarr's retries then failed each
/// probe the same way, the backoff doubled towards its 15-minute cap, and — because the breaker
/// is per source, not per operation — every search in that window was served from cache without
/// an upstream call. A setting on the download path had silently blacked out search.
/// </para>
/// </remarks>
public sealed class UpstreamRedirectRefusedException : Exception
{
    public UpstreamRedirectRefusedException(string sourceName, int statusCode)
        : base($"Source '{sourceName}' answered the download request with HTTP {statusCode} (a redirect) instead of the payload. "
               + "Arbitarr does not follow download redirects. If the source is NZBHydra2, set Downloading > NZB access type to 'Proxy' rather than 'Redirect to indexer'.")
    {
        SourceName = sourceName;
        StatusCode = statusCode;
    }

    public string SourceName { get; }

    public int StatusCode { get; }
}
