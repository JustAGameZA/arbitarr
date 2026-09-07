using System.Diagnostics;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Caching;
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
        FilterStage filterStage,
        InMemoryReleaseLookup releaseLookup,
        RecentSearchLog recentSearchLog,
        IEventSink eventSink,
        HttpRequest request,
        CancellationToken cancellationToken,
        int? tvdbId = null,
        int? tmdbId = null,
        int? season = null,
        int? episode = null,
        string? clientName = null)
    {
        var (result, rateLimited) = await ExecuteAsync(searchType, queryText, categories, limit, offset, tvdbId, tmdbId, season, episode, snapshotService, filterStage, releaseLookup, recentSearchLog, eventSink, clientName, cancellationToken).ConfigureAwait(false);
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
        FilterStage filterStage,
        InMemoryReleaseLookup releaseLookup,
        RecentSearchLog recentSearchLog,
        IEventSink eventSink,
        HttpRequest request,
        CancellationToken cancellationToken,
        int? tvdbId = null,
        int? tmdbId = null,
        int? season = null,
        int? episode = null,
        string? clientName = null)
    {
        var (result, rateLimited) = await ExecuteAsync(searchType, queryText, categories, limit, offset, tvdbId, tmdbId, season, episode, snapshotService, filterStage, releaseLookup, recentSearchLog, eventSink, clientName, cancellationToken).ConfigureAwait(false);
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
        FilterStage filterStage,
        InMemoryReleaseLookup releaseLookup,
        RecentSearchLog recentSearchLog,
        IEventSink eventSink,
        string? clientName,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var query = new SearchQuery(queryText, categories, limit, offset, tvdbId, tmdbId, season, episode);
        var result = await snapshotService.GetPageAsync(searchType ?? "search", query, cancellationToken).ConfigureAwait(false);

        // Only surface the rate-limit element when every configured source failed with
        // RequestLimitReachedException and none contributed any results — a partially degraded
        // merge (some sources rate-limited, others succeeded) still renders normally.
        var rateLimited = result.Releases.Count == 0 && result.RateLimitedSources.Count > 0;
        if (rateLimited)
        {
            return (null, true);
        }

        // Filter before anything downstream sees the set (M4-7): the recorded result count, the
        // download-proxy registrations and the rendered XML must all agree on the post-filter view.
        var filtered = await filterStage.ApplyAsync(result.Releases, queryText ?? string.Empty, clientName, cancellationToken).ConfigureAwait(false);

        // Register only the post-filter set: an enforced-mode (shadow OFF) suppression is a deny,
        // full stop, so a withheld release must not remain resolvable via /download/{proxyGuid}.
        // Shadow-mode-suppressed releases stay in `filtered` (annotated), so they stay
        // downloadable.
        releaseLookup.RecordRange(filtered);

        stopwatch.Stop();

        // Record SearchQuery.QueryText (the parsed query term), never the raw HttpRequest — the
        // client's apikey travels on the request's query string, and RecentSearchLog feeds the
        // unauthenticated /api/searches/recent dashboard (M2-5). ResolvedIdentity stays null until
        // M5 wires identity resolution into this path.
        recentSearchLog.Record(new RecentSearchEntry(
            ReceivedAt: DateTimeOffset.UtcNow,
            Query: query.QueryText ?? string.Empty,
            ResolvedIdentity: null,
            ResultCount: filtered.Count,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            Band: result.CacheBand.ToString().ToLowerInvariant()));

        // #55 step 2, AC3: the durable counterpart to the RecentSearchLog line above. That log is a
        // process-lifetime ring buffer and forgets everything on restart (see its own doc comment,
        // and System.tsx:123-124 on the pipeline counters); this row survives one, which is the
        // visible difference the Activity surface exists to provide (AC4).
        //
        // AC3 asks whether a search was served from cache or from a live query. Deciding that needs
        // BOTH of the two caches in front of this line to be consulted, and neither CacheBand nor
        // CacheAge answers it alone:
        //
        //  - CacheBand.Fresh means "a fresh cache hit OR a just-completed upstream fetch" (see
        //    CacheStageResult's doc comment). Branching on the band alone therefore labels every
        //    live fan-out "served from cache" — an integration test caught exactly that here.
        //  - CacheAge separates those two (a served entry carries its real age; a just-completed
        //    fetch is stamped TimeSpan.Zero), but only for the two-age cache. It is blind to the
        //    pagination snapshot in front of it, which REPLAYS the age of whichever request
        //    materialized the snapshot — so a snapshot hit built from a live fetch also reports
        //    Age=0 despite making no upstream call of its own.
        //
        // Hence ServedFromSnapshot, which the snapshot layer sets because it is the only layer that
        // knows. Checked FIRST: a snapshot hit is cache-served regardless of the provenance it
        // happens to be replaying. StaleButValid stays its own case — served immediately from cache
        // with a refresh attempted alongside, which is honestly neither of the other two.
        //
        // The query text goes in the REASON, following the RecentSearchLog precedent directly above:
        // the PARSED query term, never the raw HttpRequest, because the client's apikey travels on
        // that request's query string and /api/activity is un-gated exactly as
        // /api/searches/recent is.
        // CacheAge is nullable and all THREE cases matter, so the null arm is written out rather
        // than left to lifted-comparison semantics: SearchResultCacheStage returns a null age on
        // the degraded-empty path, and that path returns CacheBand.Expired after a real upstream
        // attempt — so "no age information" means a live query was made, not a cache hit. Lifted
        // `null > TimeSpan.Zero` happens to be false, which is the right answer, but relying on
        // that made the code correct for a reason no reader could see.
        var servedWithoutUpstreamCall =
            result.ServedFromSnapshot || (result.CacheAge is { } cacheAge && cacheAge > TimeSpan.Zero);
        await eventSink.RecordAsync(
            RecordedEventKind.SearchServed,
            summary: result.CacheBand switch
            {
                CacheBand.StaleButValid => $"Search served from cache while refreshing ({filtered.Count} results)",
                _ when servedWithoutUpstreamCall => $"Search served from cache ({filtered.Count} results)",
                _ => $"Search served from a live query ({filtered.Count} results)",
            },
            reason: $"Query '{query.QueryText ?? string.Empty}' ({searchType ?? "search"}), {stopwatch.Elapsed.TotalMilliseconds:F0}ms",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result with { Releases = filtered }, false);
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
