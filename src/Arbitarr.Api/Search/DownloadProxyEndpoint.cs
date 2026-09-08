using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Search;

/// <summary>
/// Fetches an upstream release's download payload (torrent or NZB) back to the caller, resolving
/// the proxy guid emitted by <see cref="SearchEndpoint"/> back to its originating
/// <see cref="IUpstreamSource"/> via <see cref="IReleaseLookup"/>. This path performs zero
/// database writes and invokes no AI logic.
///
/// SEC-L3: the upstream body is read into memory through <see cref="MaxLengthStream"/> (bounded
/// to <see cref="MaxLengthStream.MaxBytes"/>) BEFORE any response write, so a payload that exceeds
/// the cap is rejected with a clean 502 rather than a partially-written response. An earlier
/// version of this endpoint used <c>Results.Stream</c>, which commits response headers/status
/// before the body is fully read — a <see cref="DownloadTooLargeException"/> thrown mid-stream
/// could not produce a clean pre-write 502. Buffering (bounded by the same 10 MiB cap) closes that
/// gap; the memory cost is bounded by design, not unbounded buffering.
///
/// SEC-L1/amendment: the proxy guid alone only prevents enumeration of releases — it is not an
/// authorization credential. The caller must also present the client apikey it was originally
/// issued (embedded by <see cref="SearchEndpoint"/> in the rendered download link) and it is
/// re-validated here via <see cref="IClientApiKeyResolver"/>. Missing/invalid keys receive a bare
/// 401 (no XML body — this is not a Torznab/Newznab protocol response), and the endpoint fails
/// closed if no client keys are configured at all.
///
/// #97: since the resolver became DB-backed, "the client apikey" means an environment key OR a key
/// minted in Settings > API keys, of either scope — this route is PublicRead and requires no admin
/// scope. A revoked minted key is refused here on the next request, with the same bare 401.
/// </summary>
public static class DownloadProxyEndpoint
{
    public static async Task<IResult> HandleAsync(
        string proxyGuid,
        string? apikey,
        IClientApiKeyResolver apiKeyResolver,
        IReleaseLookup releaseLookup,
        IReadOnlyList<IUpstreamSource> sources,
        CancellationToken cancellationToken,
        IEventSink? eventSink = null)
    {
        // Optional and last so the many existing call sites (tests included) keep compiling; the
        // Host passes the real sink. Null means "do not record", never "fail".
        eventSink ??= NullEventSink.Instance;

        if (await apiKeyResolver.ResolveAsync(apikey, cancellationToken).ConfigureAwait(false) is null)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var release = await releaseLookup.FindAsync(proxyGuid, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return Results.NotFound();
        }

        var source = sources.FirstOrDefault(s => s.Name == release.SourceName);
        if (source is null)
        {
            return Results.NotFound();
        }

        try
        {
            var stream = await source.FetchDownloadAsync(release.Candidate, cancellationToken).ConfigureAwait(false);
            await using var bounded = new MaxLengthStream(stream);
            using var buffer = new MemoryStream();
            await bounded.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Results.Bytes(buffer.ToArray(), "application/octet-stream");
        }
        catch (RequestLimitReachedException)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        catch (SourceUnavailableException)
        {
            // The source's circuit breaker is open: it is resting after earlier failures, not
            // broken for this request. 503 is the retryable answer Sonarr/Radarr already back off
            // on; the bare InvalidOperationException this used to be escaped as an unhandled 500
            // on every retry for as long as the breaker stayed open.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (UpstreamRedirectRefusedException ex)
        {
            // A redirect is a configuration answer from a healthy upstream (NZBHydra2's "NZB
            // access type: Redirect to indexer"), and it will repeat on every download until the
            // operator changes that setting. It is recorded as an event so the fix is visible on
            // the dashboard rather than only in a swallowed exception; the source deliberately
            // does not count it against the breaker (see the exception's remarks).
            await eventSink.RecordAsync(
                RecordedEventKind.SourceFailed,
                summary: $"Download refused: {release.SourceName} redirected instead of serving the file",
                reason: ex.Message,
                sourceDisplayName: release.SourceName,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (DownloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException)
        {
            // SEC-M1: covers both a genuinely failed upstream request and the origin-mismatch
            // guard NzbHydraSource.FetchDownloadAsync throws when the resolved link's
            // scheme/host/port no longer matches the configured upstream origin at fetch time.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
