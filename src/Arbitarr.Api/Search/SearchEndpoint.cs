using System.Diagnostics;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Search;

/// <summary>
/// Serves <c>t=search|tvsearch|movie|music</c> for both the Torznab and Newznab indexer
/// endpoints: fans the query out to every configured <see cref="IUpstreamSource"/> via
/// <see cref="UpstreamMergeStage"/>, then renders the merged, source-tagged result set as the
/// appropriate protocol family's search-results XML. Rendering is a pure passthrough — no
/// normalization is applied to title, size, category, or guid (M1-4).
///
/// A source that fails with <see cref="RequestLimitReachedException"/> (M1-9) does not fail the
/// whole request: if at least one source still returned results, those are rendered normally;
/// only when every contributing source is either rate-limited or empty does this render the
/// Torznab/Newznab rate-limit error element instead of an empty (but successful-looking) result
/// set — this is never surfaced as a 5xx.
/// </summary>
public static class SearchEndpoint
{
    /// <summary>Torznab/Newznab error code for "request limit reached" per M1-9.</summary>
    public const int RateLimitErrorCode = 500;

    public static async Task<IResult> HandleTorznabAsync(
        string? searchType,
        string? queryText,
        IReadOnlyList<int> categories,
        int limit,
        int offset,
        string callerApiKey,
        PaginationSnapshotService snapshotService,
        InMemoryReleaseLookup releaseLookup,
        RecentSearchLog recentSearchLog,
        HttpRequest request,
        CancellationToken cancellationToken,
        int? tvdbId = null,
        int? tmdbId = null,
        int? season = null,
        int? episode = null)
    {
        var (result, rateLimited) = await ExecuteAsync(searchType, queryText, categories, limit, offset, tvdbId, tmdbId, season, episode, snapshotService, releaseLookup, recentSearchLog, cancellationToken).ConfigureAwait(false);
        if (rateLimited)
        {
            var errorXml = TorznabXmlWriter.WriteError(RateLimitErrorCode, "Request limit reached");
            return Results.Text(XmlDocumentRendering.ToXmlString(errorXml), TorznabXmlWriter.ContentType);
        }

        var xml = TorznabXmlWriter.WriteSearchResults(result!.Releases, r => DownloadLink(request, r, callerApiKey), result.CacheAge, result.CacheBand);
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), TorznabXmlWriter.ContentType);
    }

    public static async Task<IResult> HandleNewznabAsync(
        string? searchType,
        string? queryText,
        IReadOnlyList<int> categories,
        int limit,
        int offset,
        string callerApiKey,
        PaginationSnapshotService snapshotService,
        InMemoryReleaseLookup releaseLookup,
        RecentSearchLog recentSearchLog,
        HttpRequest request,
        CancellationToken cancellationToken,
        int? tvdbId = null,
        int? tmdbId = null,
        int? season = null,
        int? episode = null)
    {
        var (result, rateLimited) = await ExecuteAsync(searchType, queryText, categories, limit, offset, tvdbId, tmdbId, season, episode, snapshotService, releaseLookup, recentSearchLog, cancellationToken).ConfigureAwait(false);
        if (rateLimited)
        {
            var errorXml = NewznabXmlWriter.WriteError(RateLimitErrorCode, "Request limit reached");
            return Results.Text(XmlDocumentRendering.ToXmlString(errorXml), NewznabXmlWriter.ContentType);
        }

        var xml = NewznabXmlWriter.WriteSearchResults(result!.Releases, r => DownloadLink(request, r, callerApiKey), result.CacheAge, result.CacheBand);
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), NewznabXmlWriter.ContentType);
    }

    private static async Task<(PagedMergeResult? Result, bool RateLimited)> ExecuteAsync(
        string? searchType,
        string? queryText,
        IReadOnlyList<int> categories,
        int limit,
        int offset,
        int? tvdbId,
        int? tmdbId,
        int? season,
        int? episode,
        PaginationSnapshotService snapshotService,
        InMemoryReleaseLookup releaseLookup,
        RecentSearchLog recentSearchLog,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = new SearchQuery(queryText, categories, limit, offset, tvdbId, tmdbId, season, episode);
        var result = await snapshotService.GetPageAsync(searchType ?? "search", query, cancellationToken).ConfigureAwait(false);

        releaseLookup.RecordRange(result.Releases);

        // Only surface the rate-limit element when every configured source failed with
        // RequestLimitReachedException and none contributed any results — a partially degraded
        // merge (some sources rate-limited, others succeeded) still renders normally.
        var rateLimited = result.Releases.Count == 0 && result.RateLimitedSources.Count > 0;

        stopwatch.Stop();

        // Record SearchQuery.QueryText (the parsed query term), never the raw HttpRequest — the
        // client's apikey travels on the request's query string, and RecentSearchLog feeds the
        // unauthenticated /api/searches/recent dashboard (M2-5). Band/ResolvedIdentity stay null
        // until M5/M3 wire cache-band and identity resolution into this path.
        recentSearchLog.Record(new RecentSearchEntry(
            ReceivedAt: DateTimeOffset.UtcNow,
            Query: query.QueryText ?? string.Empty,
            ResolvedIdentity: null,
            ResultCount: result.Releases.Count,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            Band: null));

        return (result, rateLimited);
    }

    // The proxy guid alone only prevents enumeration of releases; it is not an authorization
    // credential. Embedding the caller's own resolved apikey here means DownloadProxyEndpoint can
    // re-check it before streaming, so a link copied/leaked from one client's response cannot be
    // used by a party who never had that client's key.
    private static Uri DownloadLink(HttpRequest request, RenderedRelease release, string callerApiKey)
    {
        var baseUri = $"{request.Scheme}://{request.Host}";
        var apikeyQuery = Uri.EscapeDataString(callerApiKey);
        return new Uri(
            $"{baseUri}/download/{Uri.EscapeDataString(release.ProxyGuid)}?apikey={apikeyQuery}",
            UriKind.Absolute);
    }
}
