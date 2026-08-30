using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Search;

/// <summary>
/// Streams an upstream release's download payload (torrent or NZB) back to the caller, resolving
/// the proxy guid emitted by <see cref="SearchEndpoint"/> back to its originating
/// <see cref="IUpstreamSource"/> via <see cref="IReleaseLookup"/>. This path performs zero
/// database writes and invokes no AI logic: it is a pure, streaming pass-through from the
/// upstream source's <see cref="IUpstreamSource.FetchDownloadAsync"/> to the HTTP response body,
/// never buffering the payload in memory (bounded to <see cref="MaxLengthStream.MaxBytes"/> per
/// SEC-L3).
///
/// SEC-L1/amendment: the proxy guid alone only prevents enumeration of releases — it is not an
/// authorization credential. The caller must also present the client apikey it was originally
/// issued (embedded by <see cref="SearchEndpoint"/> in the rendered download link) and it is
/// re-validated here via <see cref="IClientApiKeyResolver"/>. Missing/invalid keys receive a bare
/// 401 (no XML body — this is not a Torznab/Newznab protocol response), and the endpoint fails
/// closed if no client keys are configured at all.
/// </summary>
public static class DownloadProxyEndpoint
{
    public static async Task<IResult> HandleAsync(
        string proxyGuid,
        string? apikey,
        IClientApiKeyResolver apiKeyResolver,
        IReleaseLookup releaseLookup,
        IReadOnlyList<IUpstreamSource> sources,
        CancellationToken cancellationToken)
    {
        if (apiKeyResolver.Resolve(apikey) is null)
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
            return Results.Stream(new MaxLengthStream(stream), "application/octet-stream");
        }
        catch (RequestLimitReachedException)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        catch (DownloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
