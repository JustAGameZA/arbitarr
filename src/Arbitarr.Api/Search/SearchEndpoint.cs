using System.Diagnostics;
using System.Globalization;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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
        string? clientName = null,
        // arb-u1c: optional and last so every pre-existing caller (and the eight golden/rendering
        // tests that construct this call directly) keeps compiling unchanged. Null means "no
        // resolver", which is also the runtime state whenever no Sonarr instance is configured, so
        // the default is the honest one rather than a convenience.
        IIdentityResolver? identityResolver = null,
        // arb-tps: optional and last for the same reason identityResolver above is. Null means
        // "memory only" — the pre-arb-tps behaviour — which keeps the rendering/golden tests that
        // construct this call directly compiling and unconcerned with persistence.
        IReleaseLookupStore? releaseLookupStore = null,
        // arb-zwk: optional and last for the same reason the two above are. Null means "no logging",
        // which is the honest state for the rendering/golden tests that call this directly — a
        // degraded store write is still degraded, it just goes unrecorded where nothing observes it.
        ILogger? logger = null)
    {
        var (result, rateLimited) = await ExecuteAsync(SearchProtocol.Torznab, searchType, queryText, categories, limit, offset, tvdbId, tmdbId, season, episode, snapshotService, filterStage, releaseLookup, recentSearchLog, eventSink, identityResolver, clientName, releaseLookupStore, logger, cancellationToken).ConfigureAwait(false);
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
        string? clientName = null,
        // arb-u1c: optional and last so every pre-existing caller (and the eight golden/rendering
        // tests that construct this call directly) keeps compiling unchanged. Null means "no
        // resolver", which is also the runtime state whenever no Sonarr instance is configured, so
        // the default is the honest one rather than a convenience.
        IIdentityResolver? identityResolver = null,
        // arb-tps: see HandleTorznabAsync's note on this parameter.
        IReleaseLookupStore? releaseLookupStore = null,
        // arb-zwk: see HandleTorznabAsync's note on this parameter.
        ILogger? logger = null)
    {
        var (result, rateLimited) = await ExecuteAsync(SearchProtocol.Newznab, searchType, queryText, categories, limit, offset, tvdbId, tmdbId, season, episode, snapshotService, filterStage, releaseLookup, recentSearchLog, eventSink, identityResolver, clientName, releaseLookupStore, logger, cancellationToken).ConfigureAwait(false);
        if (rateLimited)
        {
            var errorXml = NewznabXmlWriter.WriteError(RateLimitErrorCode, "Request limit reached");
            return Results.Text(XmlDocumentRendering.ToXmlString(errorXml), NewznabXmlWriter.ContentType);
        }

        var xml = NewznabXmlWriter.WriteSearchResults(result!.Releases, r => DownloadLink(request, r, callerApiKey), result.CacheAge, result.CacheBand);
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), NewznabXmlWriter.ContentType);
    }

    private static async Task<(PagedMergeResult? Result, bool RateLimited)> ExecuteAsync(
        SearchProtocol protocol,
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
        IIdentityResolver? identityResolver,
        string? clientName,
        IReleaseLookupStore? releaseLookupStore,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        // #104: the inbound t= now reaches the source, so an id-less tvsearch keeps its mode and its
        // season/ep instead of being downgraded to a plain search upstream. Parsed by explicit name
        // match (CLAUDE.md §3) — never Enum.TryParse, which would also accept "t=1".
        var parsedType = SearchTypeParser.Parse(searchType);
        var query = new SearchQuery(queryText, categories, limit, protocol, offset, tvdbId, tmdbId, season, episode, parsedType);

        // arb-u1c: resolve the id to a series title BEFORE the query goes to the cache stage, because
        // both things downstream need it are downstream of here -- the cache key (which needs the
        // absolute number to stop two episodes of one series sharing a row) and the upstream URI
        // (which needs the title to stop a bare number becoming a feed-wide search). Both the
        // snapshot token and the two-age key are derived from the query AFTER this runs, so the
        // resolved title cannot leak across rows that differ by it.
        query = await ResolveAnimeIdentityAsync(query, identityResolver, cancellationToken).ConfigureAwait(false);

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

        // arb-tps: the durable counterpart to the line above, written to the SAME post-filter set
        // for the same reason — an enforced suppression must not stay resolvable via /download in
        // either tier. Awaited rather than fired-and-forgotten: a batch insert against a local
        // SQLite file is cheap, and this is the search path, not the download hot path. A link that
        // is handed out before its row is committed would be exactly the 404 this exists to fix.
        //
        // Null when no store was supplied (the rendering/golden tests), which reproduces the old
        // memory-only behaviour rather than failing.
        //
        // arb-zwk: the write is still AWAITED — everything above still holds — but a FAILED write is
        // logged and the search still answers, matching PersistentReleaseLookup's posture on the read
        // side. Degrading is right here because the failure is not fatal to the caller: the memory
        // tier still resolves the link for its 30 minutes, and past that the download path already
        // degrades a store miss to a 404 rather than a 500. Letting the exception escape would turn a
        // durability problem into a total search outage — an *arr sees no results at all, rather than
        // results whose links merely stop surviving a restart.
        if (releaseLookupStore is not null && filtered.Count > 0)
        {
            try
            {
                await releaseLookupStore.UpsertRangeAsync(
                        filtered.Select(r => new StoredRelease(r.ProxyGuid, r.SourceName, r.Candidate)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A cancelled request is not a store fault: the caller went away, and swallowing it
                // here would report success for a search nobody is waiting for.
                throw;
            }
            catch (Exception ex)
            {
                // Count and exception only — never the releases or their links. This lands in the
                // persistent log store served at /api/admin/logs (CLAUDE.md §1), and a release payload
                // carries the source URL, which is the one thing that must not be written there.
                logger?.LogWarning(
                    ex,
                    "Release lookup store write failed for {ReleaseCount} releases; the search still answered and the in-memory tier still resolves these links.",
                    filtered.Count);
            }
        }

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
        //
        // arb-2b6: that rule is unchanged — what changed is that the text ALONE did not identify the
        // search. Every RSS and id-based search carries no text, so 55 events all read
        // `Query '' (tvsearch)` and a zero-result cache hit could not be told from a live query with
        // real results fired in the same second (audit F-010). SearchQueryDescriptor renders the
        // whole identifying shape — categories, ids, season/episode, absolute — in two spellings:
        // one for the reason, one stable and machine-readable for the detail. It is built from the
        // SearchQuery record, so the boundary above still holds by construction: the record carries
        // no credential. Note it takes the mode from `query.Type` (parsed, closed to three values)
        // rather than the raw `searchType` string this reason used to interpolate, so a caller's
        // arbitrary `t=` value is no longer echoed into an un-gated feed.
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
            reason: $"{SearchQueryDescriptor.Describe(query)}, {stopwatch.Elapsed.TotalMilliseconds:F0}ms",
            detail: SearchQueryDescriptor.DescribeDetail(query),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (result with { Releases = filtered }, false);
    }

    /// <summary>
    /// arb-u1c: for Sonarr's anime episode shape only, records the absolute episode number on the
    /// query and asks the identity resolver for the series title that number belongs to.
    /// </summary>
    /// <remarks>
    /// <para><b>THE SHAPE THIS TARGETS.</b> Sonarr searches an anime episode as
    /// <c>t=tvsearch&amp;tvdbid=X&amp;q=NN</c>, where <c>NN</c> is a bare ABSOLUTE episode number
    /// and there is no <c>season</c> or <c>ep</c> parameter at all. Every other request shape is
    /// left exactly as it arrived, so this cannot change the query, the cache key, or the upstream
    /// URI for anything that was working before. The classification itself is
    /// <see cref="SearchQuery.IsIdScopedAbsoluteNumberQuery"/> — ONE predicate, shared with
    /// <c>NzbHydraSource</c>, which builds the upstream URL from the same answer. It used to be two
    /// copies and they drifted; see that method's remarks.</para>
    ///
    /// <para><b>THE ABSOLUTE NUMBER IS RECORDED EVEN WHEN NOTHING RESOLVES.</b> That is the half of
    /// this that fixes a wrong answer rather than an imprecise one — see
    /// <c>SearchResultCacheStage.BuildNumbering</c>. Resolution failing costs precision upstream;
    /// omitting the number from the key serves episode 91's results for episode 92.</para>
    ///
    /// <para><b><c>q=0</c> RECORDS ABSOLUTE 0, AND THAT IS THE CORRECT ANSWER.</b> Zero is a real
    /// distinct value here rather than a stand-in for "none": <c>SearchCacheKeyBuilder</c> renders
    /// an absent absolute as <c>abs=none</c> and a present zero as <c>abs=0</c>, so the two do not
    /// collide, and a request for <c>q=0</c> keeps its own cache row instead of sharing the row of
    /// every request that carried no number. No *arr sends absolute episode 0 in practice — anime
    /// absolute numbering starts at 1 — so this is about what the key does with a value it should
    /// never see, and the honest handling is to carry it through rather than to special-case it
    /// into ambiguity. <c>q=00000</c> parses to the same 0 and shares that row, correctly: they are
    /// the same number, and collapsing spellings of one number is exactly what the id-scoped key is
    /// for.</para>
    ///
    /// <para>A null <paramref name="identityResolver"/> means no resolver is registered, which is
    /// the normal state when no Sonarr instance has been configured. Resolution never fails the
    /// search: any exception from the resolver leaves the query with its absolute number and no
    /// title, which degrades to exactly the id-only upstream request that shipped before this
    /// existed. The resolver imposes its own wall-clock bound on the lookup
    /// (<c>SeriesTitleResolver.LookupBudget</c>) and memoises the answer per series, so a cached
    /// answer costs nothing and an unreachable Sonarr cannot add its full client timeout to every
    /// anime search.</para>
    /// </remarks>
    private static async Task<SearchQuery> ResolveAnimeIdentityAsync(
        SearchQuery query,
        IIdentityResolver? identityResolver,
        CancellationToken cancellationToken)
    {
        if (!query.IsIdScopedAbsoluteNumberQuery(out var absolute) || query.TvdbId is not { } tvdbId)
        {
            return query;
        }

        query = query with { Absolute = absolute };

        if (identityResolver is null)
        {
            return query;
        }

        try
        {
            var identity = await identityResolver
                .ResolveAsync(
                    query.QueryText?.Trim() ?? string.Empty,
                    new IdentityResolutionHints(tvdbId, TmdbId: null, Year: null),
                    cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(identity?.PrimaryTitle)
                ? query
                : query with { ResolvedTitle = identity.PrimaryTitle };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A search must still answer in Torznab/Newznab XML. An identity lookup is an optimisation
            // of the upstream query, never a precondition for issuing it.
            return query;
        }
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
