using System.Globalization;
using System.Xml.Linq;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Sources.NzbHydra;

/// <summary>
/// <see cref="IUpstreamSource"/> implementation backed by a real NZBHydra2 instance's
/// Newznab-compatible API, at whichever endpoint the inbound request's protocol selects:
/// <c>{base}/torznab/api</c> for <see cref="SearchProtocol.Torznab"/> and <c>{base}/api</c> for
/// <see cref="SearchProtocol.Newznab"/>. Search and caps both follow that rule.
///
/// <para>
/// The endpoint is not cosmetic (#99). NZBHydra2 reads a request to <c>/torznab/api</c> as "a
/// torrent search is requested" and excludes every usenet indexer from the selection, so issuing a
/// Newznab caller's search there returns zero usenet results while still looking like a successful,
/// empty search. The search type follows the same principle: <c>t=tvsearch</c> with
/// <c>tvdbid</c>/<c>season</c>/<c>ep</c> and <c>t=movie</c> with <c>tmdbid</c> are sent as their own
/// parameters when the query carries those ids, never folded into <c>q</c>. The one shape where
/// <c>q</c> is withheld is Sonarr's anime search (<c>tvdbid</c> plus a bare episode number as
/// <c>q</c>) — see <see cref="IsIdScopedAbsoluteNumberQuery"/> for why NZBHydra2 turns that into a
/// feed-wide search for the number.
/// </para>
///
/// <para>
/// Upstream fan-out: NZBHydra2 caps a single request at <see cref="NzbHydraSourceOptions.MaxUpstreamPageSize"/>
/// (100) results. When the caller's requested <see cref="SearchQuery.Limit"/> exceeds that, this
/// class issues multiple paged requests using <see cref="SearchQuery.Offset"/> and concatenates
/// the results, up to <see cref="NzbHydraSourceOptions.MaxUpstreamCallsPerSearch"/> total upstream
/// calls per <see cref="SearchAsync"/> invocation (default 3, i.e. up to 300 results). This cap
/// exists because AC14 requires the entire SearchAsync call to complete within a ≤12s budget:
/// docs/step0-measurements.md §4 measured a single 100-result fan-out call at 2.2s-9.1s in the
/// worst observed case, so even two sequential calls could approach the budget; capping at 3 calls
/// bounds worst-case sequential upstream time at roughly 3x the single-call worst case while still
/// allowing meaningfully larger result sets than a single page. Fan-out stops early once a page
/// returns fewer than a full page (no more results available upstream).
/// </para>
/// </summary>
public sealed class NzbHydraSource : IUpstreamSource
{
    private readonly NzbHydraSourceOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly RateLimiter _rateLimiter;

    public NzbHydraSource(
        NzbHydraSourceOptions options,
        HttpClient httpClient,
        IAsyncCircuitBreaker circuitBreaker,
        RateLimiter? rateLimiter = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _rateLimiter = rateLimiter ?? new RateLimiter(options.RateLimitMaxCalls, options.EffectiveRateLimitInterval);

        _httpClient.Timeout = options.EffectiveRequestTimeout;
    }

    public string Name => _options.SourceName;

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<ReleaseCandidate>();
        }

        var results = new List<ReleaseCandidate>();
        var remaining = query.Limit;
        var offset = query.Offset;
        var callsIssued = 0;

        while (remaining > 0 && callsIssued < _options.MaxUpstreamCallsPerSearch)
        {
            var pageSize = Math.Min(remaining, _options.MaxUpstreamPageSize);
            var pageUri = BuildSearchUri(query, pageSize, offset);

            IReadOnlyList<ReleaseCandidate> page;
            try
            {
                await _rateLimiter.WaitForTokenAsync(cancellationToken).ConfigureAwait(false);

                if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                using var response = await _httpClient.GetAsync(pageUri, cancellationToken).ConfigureAwait(false);
                ThrowIfRateLimited(response);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                page = ParseTorznabResponse(body, _options.BaseUrl);
                await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _circuitBreaker.RecordFailureAsync(Name, ex, cancellationToken).ConfigureAwait(false);
                throw;
            }

            callsIssued++;
            results.AddRange(page);
            remaining -= page.Count;
            offset += page.Count;

            if (page.Count < pageSize)
            {
                // Upstream returned fewer results than requested: no more pages available.
                break;
            }
        }

        return results;
    }

    public async Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default)
    {
        if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
        {
            return new SourceCaps(Array.Empty<int>(), false, false, null);
        }

        var uri = BuildCapsUri(protocol);

        try
        {
            await _rateLimiter.WaitForTokenAsync(cancellationToken).ConfigureAwait(false);

            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            ThrowIfRateLimited(response);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var caps = ParseCapsResponse(body);
            await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            return caps;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _circuitBreaker.RecordFailureAsync(Name, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        // SEC-M1: parse-time origin pinning (TryValidateOriginPinnedLink, above) only guarantees the
        // link was well-formed and same-origin at *parse* time — it is not a fetch-time guarantee.
        // Re-validate scheme+host+port against the configured upstream origin immediately before
        // issuing the request, so a link that was valid when parsed but has since been substituted
        // (e.g. a mutable ReleaseCandidate, or a future code path that skips parsing) cannot cause an
        // SSRF fetch to an arbitrary host.
        if (!TryValidateOriginPinnedLink(release.Link?.ToString(), _options.BaseUrl, out var validatedLink))
        {
            throw new HttpRequestException($"Refusing to fetch download link for source '{Name}': link is not same-origin with the configured upstream at fetch time.");
        }

        if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
        {
            throw new SourceUnavailableException(Name);
        }

        try
        {
            await _rateLimiter.WaitForTokenAsync(cancellationToken).ConfigureAwait(false);

            var response = await _httpClient.GetAsync(validatedLink, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfRateLimited(response);
                ThrowIfRedirected(response);
                response.EnsureSuccessStatusCode();
            }
            catch
            {
                // The response is deliberately not using-scoped, because on success its content
                // stream is handed to the caller. When a guard throws nobody else will dispose it,
                // and a redirect repeats on every retry until the operator changes the setting, so
                // an undisposed connection here would be a sustained leak rather than a one-off.
                response.Dispose();
                throw;
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch (UpstreamRedirectRefusedException)
        {
            // Recorded as a success, not merely left alone: the upstream answered promptly, and the
            // breaker's HalfOpen state is only left by a RecordSuccess or a RecordFailure. A probe
            // that recorded neither would strand the breaker HalfOpen, refusing every caller —
            // search included — until something else on this source recorded an outcome.
            await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _circuitBreaker.RecordFailureAsync(Name, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Refuses a redirect on the download path as a typed, non-breaker answer. The client is built
    /// with <c>AllowAutoRedirect = false</c> (SEC-M1), so a 3xx reaches here as-is; before this
    /// check <c>EnsureSuccessStatusCode</c> turned it into a generic <see cref="HttpRequestException"/>
    /// that the catch below counted against the breaker. See
    /// <see cref="UpstreamRedirectRefusedException"/> for why that took search down with it. The
    /// whole 3xx range is refused, 304 included: the download request sends no conditional
    /// headers, so a 304 cannot legitimately arise and is treated like any other non-payload answer.
    /// </summary>
    private void ThrowIfRedirected(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new UpstreamRedirectRefusedException(Name, status);
        }
    }

    /// <summary>
    /// SEC-M2: translates an upstream 429 (Too Many Requests) or 503 (Service Unavailable) into
    /// the designed <see cref="RequestLimitReachedException"/> signal before
    /// <c>EnsureSuccessStatusCode()</c> would otherwise turn it into a bare
    /// <see cref="HttpRequestException"/> — that path was previously dead, so a real upstream
    /// rate limit surfaced as an unhandled 500 on the proxy and a silently-empty search result
    /// instead of the Torznab/Newznab rate-limit element.
    /// </summary>
    private void ThrowIfRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw new RequestLimitReachedException(Name);
        }
    }

    /// <summary>
    /// The upstream path for an inbound protocol family (#99). NZBHydra2 does not treat these two
    /// as aliases: it reads <c>/torznab/api</c> as "a torrent search is requested" and drops every
    /// usenet indexer from the selection, so a Newznab caller routed here receives nothing from
    /// usenet. Selecting the path from the inbound protocol is what makes the usenet half of the
    /// broker work at all.
    /// </summary>
    private static string UpstreamPath(SearchProtocol protocol) => protocol switch
    {
        SearchProtocol.Torznab => "torznab/api",
        SearchProtocol.Newznab => "api",
        // No default arm that guesses: SearchProtocol has exactly two members and no zero value, so
        // a new one must be routed here deliberately rather than silently inheriting torrents.
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Unknown search protocol."),
    };

    /// <summary>
    /// The Newznab/Torznab <c>t=</c> mode for a query, chosen from the mode the INBOUND request
    /// asked for and, only as a fallback, from the ids the caller supplied. NZBHydra2's API is
    /// Newznab-compatible, so <c>tvsearch</c>/<c>movie</c> take <c>tvdbid</c>/<c>season</c>/
    /// <c>ep</c> and <c>tmdbid</c> respectively; a query that is neither is a plain <c>search</c>.
    /// The ids are sent as their own parameters and are never folded into <c>q</c> — upstream
    /// matches an id exactly, where the same digits inside the free-text term would just be noise
    /// that narrows the result set for no reason.
    /// </summary>
    /// <remarks>
    /// #104: the mode is the request's, NOT the ids'. Deriving it from the ids alone downgraded
    /// <c>t=tvsearch&amp;q=…&amp;season=22&amp;ep=1</c> (a Sonarr episode search that fell back to a
    /// text query, and every dashboard episode search) to a plain <c>t=search</c> and dropped the
    /// episode selector, returning the newest episode of the series instead of the requested one.
    /// NZBHydra2 accepts an id-less <c>tvsearch</c> — it issues exactly that shape to indexers
    /// itself as its own fallback query — so nothing upstream required the id.
    ///
    /// The id arms remain BELOW the request's own mode rather than being deleted: a caller that
    /// supplies a tvdbid/tmdbid without an explicit <c>t=</c> (the dashboard's ad-hoc route builds
    /// its <see cref="SearchType"/> itself, but <see cref="SearchQuery.Type"/> is defaulted, so a
    /// future construction site may not) still gets the mode that accepts the id it sent.
    /// </remarks>
    private static string SearchMode(SearchQuery query) => query switch
    {
        { Type: SearchType.TvSearch } => TvSearchMode,
        { Type: SearchType.Movie } => "movie",
        { TvdbId: not null } => TvSearchMode,
        { TmdbId: not null } => "movie",
        _ => "search",
    };

    /// <summary>The upstream <c>t=</c> value for a <c>tvsearch</c>, named once so the mode string
    /// the URI carries and the mode the id/numbering emission below branches on cannot drift.</summary>
    private const string TvSearchMode = "tvsearch";

    /// <summary>
    /// True for Sonarr's anime episode search shape — <c>t=tvsearch</c>, a <c>tvdbid</c>, and a
    /// <c>q</c> that is nothing but the absolute episode number (<c>tvdbid=81797&amp;q=92</c>,
    /// "One Piece, absolute 92"). Sonarr's <c>NewznabRequestGenerator</c> builds it as
    /// <c>ids + "&amp;q={AbsoluteEpisodeNumber:00}"</c>: the id is meant to select the series and
    /// the number to filter within it.
    /// </summary>
    /// <remarks>
    /// NZBHydra2 cannot honour that split. When a request carries a <c>q</c> it treats the text as
    /// the whole query, and for every indexer that does not support the id it drops the id and
    /// sends the bare number as the free-text term — observed 2026-09-08 as
    /// <c>t=search&amp;q=92</c> reaching Animetosho and ameNZB, which returned episode 92 of every
    /// anime on the feed and nothing for the series Sonarr asked about. A number alone identifies
    /// nothing, so it must never reach upstream as free text. With the id and no <c>q</c>, NZBHydra2
    /// passes the id to indexers that accept it and converts it to the series title for the rest —
    /// the closest shape to "this series" that it can actually execute. The exact-episode text form
    /// is not lost: Sonarr sends <c>t=search&amp;q={title}+{absolute:00}</c> alongside this request
    /// for every scene title it knows, and that one goes upstream verbatim.
    ///
    /// Scoped deliberately narrowly: only a <c>tvsearch</c> that will actually emit its
    /// <c>tvdbid</c>, and only when the text is entirely digits. A textual <c>q</c> next to an id
    /// (<c>tvdbid=74796&amp;q=bleach</c>) is a title and stays; a numeric <c>q</c> without an id has
    /// nothing else to identify the series and stays, since dropping it would turn the request into
    /// a category-wide feed. <c>char.IsAsciiDigit</c> rather than <c>char.IsDigit</c> because Sonarr
    /// formats the number with the invariant culture: only ASCII digits are the shape being matched,
    /// and a non-ASCII numeral is text like any other.
    ///
    /// <paramref name="queryText"/> is the already-trimmed text <see cref="BuildSearchUri"/> emits,
    /// so the value classified and the value sent are the same string.
    /// </remarks>
    private static bool IsIdScopedAbsoluteNumberQuery(string queryText, string mode, int? tvdbId)
    {
        if (!string.Equals(mode, TvSearchMode, StringComparison.Ordinal) || tvdbId is null)
        {
            return false;
        }

        return queryText.Length > 0 && queryText.All(char.IsAsciiDigit);
    }

    private Uri BuildSearchUri(SearchQuery query, int limit, int offset)
    {
        var mode = SearchMode(query);
        var builder = new UriBuilder(new Uri(_options.BaseUrl, UpstreamPath(query.Protocol)));
        var queryParams = new List<string>
        {
            "t=" + Uri.EscapeDataString(mode),
            "limit=" + limit.ToString(CultureInfo.InvariantCulture),
            "offset=" + offset.ToString(CultureInfo.InvariantCulture),
        };

        // Trimmed once here and the same value is both classified and emitted, so the predicate
        // cannot say "withhold" about a string different from the one that would have gone up.
        var queryText = query.QueryText?.Trim() ?? string.Empty;
        if (queryText.Length > 0 && !IsIdScopedAbsoluteNumberQuery(queryText, mode, query.TvdbId))
        {
            queryParams.Add("q=" + Uri.EscapeDataString(queryText));
        }

        // Id parameters are emitted only for the mode that accepts them, so a query carrying both a
        // tvdbid and a tmdbid does not send a tmdbid along with t=tvsearch (upstream would ignore
        // it, but it would also become part of the request identity for no benefit). Season/ep ride
        // with tvsearch, matching the Newznab parameter set NZBHydra2 advertises in its caps — but
        // with the MODE, not with the tvdbid (#104): an id-less tvsearch carries them too, which is
        // the whole point of the issue. They are still gated on the mode rather than emitted
        // unconditionally, because season/ep are not part of the movie or plain-search parameter
        // sets and sending them there would add noise to the request identity for nothing.
        if (string.Equals(mode, TvSearchMode, StringComparison.Ordinal))
        {
            if (query.TvdbId is int tvdbId)
            {
                queryParams.Add("tvdbid=" + tvdbId.ToString(CultureInfo.InvariantCulture));
            }

            if (query.Season is int season)
            {
                queryParams.Add("season=" + season.ToString(CultureInfo.InvariantCulture));
            }

            if (query.Episode is int episode)
            {
                queryParams.Add("ep=" + episode.ToString(CultureInfo.InvariantCulture));
            }
        }
        else if (query.TmdbId is int tmdbId)
        {
            queryParams.Add("tmdbid=" + tmdbId.ToString(CultureInfo.InvariantCulture));
        }

        if (query.Categories.Count > 0)
        {
            queryParams.Add("cat=" + Uri.EscapeDataString(string.Join(",", query.Categories)));
        }

        // apikey is passed as a query-string parameter, never a header, never logged.
        //
        // LOAD-BEARING (CLAUDE.md §1): IHttpClientFactory attaches its own logging handler to every
        // named client and logs the full absolute URI at Information, which since #65 lands in the
        // persistent log store. LogMessageCleanser scrubs credentials in QUERY STRINGS only — a
        // secret moved into the URL path would not be covered. So the apikey must stay here.
        queryParams.Add("apikey=" + Uri.EscapeDataString(_options.ApiKey));

        builder.Query = string.Join("&", queryParams);
        return builder.Uri;
    }

    private Uri BuildCapsUri(SearchProtocol protocol)
    {
        var builder = new UriBuilder(new Uri(_options.BaseUrl, UpstreamPath(protocol)));
        builder.Query = "t=caps&apikey=" + Uri.EscapeDataString(_options.ApiKey);
        return builder.Uri;
    }

    /// <summary>
    /// SEC-M1 (SSRF): validates an upstream-supplied <c>&lt;link&gt;</c> against the configured
    /// NZBHydra <paramref name="allowedOrigin"/> (scheme + host + port) before it is trusted as a
    /// download target. Without this, a compromised/malicious upstream feed could point
    /// <c>&lt;link&gt;</c> at an arbitrary LAN host and have Arbitarr fetch and stream it back to
    /// the caller via DownloadProxyEndpoint. Non-conforming links cause the whole item to be
    /// dropped (never defaulted to a placeholder URI, which would still be a valid, fetchable
    /// target).
    /// </summary>
    private static bool TryValidateOriginPinnedLink(string? link, Uri allowedOrigin, out Uri validated)
    {
        validated = null!;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var parsedLink))
        {
            return false;
        }

        if (parsedLink.Scheme != Uri.UriSchemeHttp && parsedLink.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.Equals(parsedLink.Host, allowedOrigin.Host, StringComparison.OrdinalIgnoreCase)
            || parsedLink.Port != allowedOrigin.Port)
        {
            return false;
        }

        validated = parsedLink;
        return true;
    }

    private static List<ReleaseCandidate> ParseTorznabResponse(string xml, Uri allowedOrigin)
    {
        var doc = XDocument.Parse(xml);
        var items = doc.Descendants("item");
        var results = new List<ReleaseCandidate>();

        foreach (var item in items)
        {
            var title = item.Element("title")?.Value ?? string.Empty;
            var guid = item.Element("guid")?.Value ?? title;
            var link = item.Element("link")?.Value;
            var pubDateRaw = item.Element("pubDate")?.Value;

            if (!TryValidateOriginPinnedLink(link, allowedOrigin, out var linkUri))
            {
                // Drop the item rather than defaulting to a placeholder URI: a placeholder would
                // still be a well-formed, fetchable target, defeating the point of the check.
                continue;
            }

            var pubDate = TryParseDate(pubDateRaw) ?? DateTimeOffset.UtcNow;

            long size = 0;
            var sizeAttr = item.Elements(TorznabNs + "attr")
                .FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, "size", StringComparison.OrdinalIgnoreCase));
            if (sizeAttr is not null && long.TryParse(sizeAttr.Attribute("value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize))
            {
                size = parsedSize;
            }
            else if (long.TryParse(item.Element("size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallbackSize))
            {
                size = fallbackSize;
            }

            var categories = item.Elements(TorznabNs + "attr")
                .Where(a => string.Equals(a.Attribute("name")?.Value, "category", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Attribute("value")?.Value)
                .Where(v => v is not null)
                .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cat) ? cat : (int?)null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray();

            var protocolAttr = item.Elements(TorznabNs + "attr")
                .FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, "protocol", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")?.Value;

            var protocol = protocolAttr?.ToLowerInvariant() switch
            {
                "torrent" => ProtocolKind.Torrent,
                "usenet" => ProtocolKind.Usenet,
                _ => item.Element("enclosure")?.Attribute("type")?.Value?.Contains("torrent", StringComparison.OrdinalIgnoreCase) == true
                    ? ProtocolKind.Torrent
                    : ProtocolKind.Usenet,
            };

            results.Add(new ReleaseCandidate
            {
                Title = title,
                Guid = guid,
                PubDate = pubDate,
                Size = size,
                Link = linkUri,
                Category = categories,
                Protocol = protocol,
            });
        }

        return results;
    }

    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";

    private static DateTimeOffset? TryParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static SourceCaps ParseCapsResponse(string xml)
    {
        var doc = XDocument.Parse(xml);

        var categories = doc.Descendants()
            .Where(element => element.Name.LocalName is "category" or "subcat")
            .Select(element => new
            {
                Id = int.TryParse(element.Attribute("id")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null,
                Name = element.Attribute("name")?.Value,
            })
            .Where(category => category.Id.HasValue)
            .ToArray();

        var categoryIds = categories
            .Select(category => category.Id!.Value)
            .Distinct()
            .ToArray();

        var categoryNames = categories
            .Where(category => string.IsNullOrWhiteSpace(category.Name) is false)
            .GroupBy(category => category.Id!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name!);

        var searchingElement = doc.Descendants("searching").FirstOrDefault();
        var supportsTv = string.Equals(
            searchingElement?.Element("tv-search")?.Attribute("available")?.Value,
            "yes",
            StringComparison.OrdinalIgnoreCase);
        var supportsMovie = string.Equals(
            searchingElement?.Element("movie-search")?.Attribute("available")?.Value,
            "yes",
            StringComparison.OrdinalIgnoreCase);

        var supportedParams = searchingElement?
            .Elements()
            .SelectMany(search => (search.Attribute("supportedParams")?.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(parameter => parameter, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        var limitsElement = doc.Descendants("limits").FirstOrDefault();
        int? maxPageSize = null;
        if (limitsElement is not null
            && int.TryParse(limitsElement.Attribute("max")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
        {
            maxPageSize = max;
        }

        return new SourceCaps(categoryIds, supportsTv, supportsMovie, maxPageSize, supportedParams, CategoryNames: categoryNames);
    }
}
