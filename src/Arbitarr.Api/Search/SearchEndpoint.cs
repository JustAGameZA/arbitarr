using Arbitarr.Api.Rendering;
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
        PaginationSnapshotService snapshotService,
        FilterStage filterStage,
        InMemoryReleaseLookup releaseLookup,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var (releases, rateLimited) = await ExecuteAsync(searchType, queryText, categories, limit, offset, snapshotService, filterStage, releaseLookup, cancellationToken).ConfigureAwait(false);
        if (rateLimited)
        {
            var errorXml = TorznabXmlWriter.WriteError(RateLimitErrorCode, "Request limit reached");
            return Results.Text(XmlDocumentRendering.ToXmlString(errorXml), TorznabXmlWriter.ContentType);
        }

        var xml = TorznabXmlWriter.WriteSearchResults(releases!, r => DownloadLink(request, r));
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), TorznabXmlWriter.ContentType);
    }

    public static async Task<IResult> HandleNewznabAsync(
        string? searchType,
        string? queryText,
        IReadOnlyList<int> categories,
        int limit,
        int offset,
        PaginationSnapshotService snapshotService,
        FilterStage filterStage,
        InMemoryReleaseLookup releaseLookup,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var (releases, rateLimited) = await ExecuteAsync(searchType, queryText, categories, limit, offset, snapshotService, filterStage, releaseLookup, cancellationToken).ConfigureAwait(false);
        if (rateLimited)
        {
            var errorXml = NewznabXmlWriter.WriteError(RateLimitErrorCode, "Request limit reached");
            return Results.Text(XmlDocumentRendering.ToXmlString(errorXml), NewznabXmlWriter.ContentType);
        }

        var xml = NewznabXmlWriter.WriteSearchResults(releases!, r => DownloadLink(request, r));
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), NewznabXmlWriter.ContentType);
    }

    private static async Task<(IReadOnlyList<RenderedRelease>? Releases, bool RateLimited)> ExecuteAsync(
        string? searchType,
        string? queryText,
        IReadOnlyList<int> categories,
        int limit,
        int offset,
        PaginationSnapshotService snapshotService,
        FilterStage filterStage,
        InMemoryReleaseLookup releaseLookup,
        CancellationToken cancellationToken)
    {
        var query = new SearchQuery(queryText, categories, limit, offset);
        var result = await snapshotService.GetPageAsync(searchType ?? "search", query, cancellationToken).ConfigureAwait(false);

        // Only surface the rate-limit element when every configured source failed with
        // RequestLimitReachedException and none contributed any results — a partially degraded
        // merge (some sources rate-limited, others succeeded) still renders normally.
        var rateLimited = result.Releases.Count == 0 && result.RateLimitedSources.Count > 0;
        if (rateLimited)
        {
            return (null, true);
        }

        var queryKey = queryText ?? string.Empty;
        var filtered = await filterStage.ApplyAsync(result.Releases, queryKey, cancellationToken).ConfigureAwait(false);

        // Register only the post-filter set: an enforced-mode (shadow OFF) suppression is a deny,
        // full stop, so a withheld release must not remain resolvable via /download/{proxyGuid}.
        // Shadow-mode-suppressed releases stay in `filtered` (annotated), so they stay
        // downloadable.
        releaseLookup.RecordRange(filtered);

        return (filtered, false);
    }

    private static Uri DownloadLink(HttpRequest request, RenderedRelease release)
    {
        var baseUri = $"{request.Scheme}://{request.Host}";
        return new Uri($"{baseUri}/download/{Uri.EscapeDataString(release.ProxyGuid)}", UriKind.Absolute);
    }
}
