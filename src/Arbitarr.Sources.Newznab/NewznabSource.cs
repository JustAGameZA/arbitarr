using System.Globalization;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Sources.Newznab;

/// <summary>
/// <see cref="IUpstreamSource"/> implementation backed by a DIRECT Newznab or Torznab indexer —
/// one adapter for both families (arb-x7w8.2), covering search and caps.
///
/// <para><b>Why one adapter and not two.</b> The two families share a request grammar and a
/// response schema; they differ in the endpoint an indexer publishes and in which
/// <c>torznab:attr</c>s it populates. Neither difference is a branch this class has to take: the
/// endpoint arrives as <see cref="NewznabSourceOptions.ApiPath"/>, and the attrs are handled by
/// <see cref="TorznabFeedParser"/>, which matches the schema NAMESPACE rather than the prefix so a
/// <c>newznab:attr</c> and a <c>torznab:attr</c> read identically.</para>
///
/// <para><b>The path comes from the row, and NZBHydra2's endpoint rule is deliberately NOT copied.</b>
/// <c>NzbHydraSource</c> selects <c>/torznab/api</c> or <c>/api</c> from the inbound protocol because
/// NZBHydra2 reads a request to <c>/torznab/api</c> as "a torrent search is requested" and drops
/// every usenet indexer from its selection (#99). That is aggregator behaviour, specific to
/// NZBHydra2. A direct indexer serves ONE feed at ONE path and has no such selection to make, so
/// applying the rule here would rewrite an operator's configured path to one the indexer does not
/// serve. <see cref="GetCapsAsync"/> therefore ignores its <c>protocol</c> argument for endpoint
/// selection for the same reason.</para>
/// </summary>
public sealed class NewznabSource : IUpstreamSource
{
    private readonly NewznabSourceOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly RateLimiter _rateLimiter;

    public NewznabSource(
        NewznabSourceOptions options,
        HttpClient httpClient,
        IAsyncCircuitBreaker circuitBreaker,
        RateLimiter? rateLimiter = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _rateLimiter = rateLimiter ?? new RateLimiter(options.RateLimitMaxCalls, options.EffectiveRateLimitInterval);

        EnsureEndpointIsOnBaseOrigin();

        // The per-source TimeoutSeconds override is honoured HERE, on the client this instance owns,
        // because IUpstreamSource has no per-call timeout in its shape. That works while each source
        // gets its own HttpClient. When the registry (arb-x7w8.4) resolves N sources it must keep
        // that true — a single client shared across sources would make the last constructed source's
        // timeout win for all of them, silently.
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
                ThrowIfRedirected(response);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                page = TorznabFeedParser.ParseFeedResponse(body, _options.BaseUrl);
                await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            }
            catch (UpstreamRedirectRefusedException)
            {
                // Recorded as a success, not merely left alone: the upstream answered promptly, and
                // the breaker's HalfOpen state is only left by a RecordSuccess or a RecordFailure. A
                // probe that recorded neither would strand the breaker HalfOpen, refusing every
                // caller until something else on this source recorded an outcome.
                await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
                throw;
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

    /// <summary>
    /// Reads the indexer's advertised capabilities. <paramref name="protocol"/> is part of the
    /// <see cref="IUpstreamSource"/> contract because an AGGREGATOR may answer caps differently per
    /// endpoint (#99); a direct indexer publishes one feed at <see cref="NewznabSourceOptions.ApiPath"/>
    /// and has only one answer, so the endpoint is not selected from it here.
    /// </summary>
    public async Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default)
    {
        if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
        {
            return new SourceCaps(Array.Empty<int>(), false, false, null);
        }

        var uri = BuildCapsUri();

        try
        {
            await _rateLimiter.WaitForTokenAsync(cancellationToken).ConfigureAwait(false);

            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            ThrowIfRateLimited(response);
            ThrowIfRedirected(response);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var caps = TorznabFeedParser.ParseCapsResponse(body);
            await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            return caps;
        }
        catch (UpstreamRedirectRefusedException)
        {
            // Same reasoning as SearchAsync's arm: the upstream answered, so the breaker must be
            // left with a recorded outcome rather than stranded HalfOpen.
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
    /// Not implemented in arb-x7w8.2, whose scope is search and caps. The download path for direct
    /// indexers is its own bead, because it carries the proxy/redirect access-mode decision
    /// (Source.NzbAccessMode) and the fetch-time re-validation of the origin-pinned link — neither
    /// of which is a parse concern, and both of which have their own security tests.
    /// </summary>
    /// <exception cref="NotSupportedException">Always. This adapter does not serve downloads yet.</exception>
    public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"Source '{Name}' does not serve downloads: the direct-indexer download path is arb-x7w8.13 (Proxy) and arb-x7w8.14 (Redirect), " +
            "not this adapter. Refusing rather than fetching, so a caller that reaches here fails " +
            "loudly instead of bypassing the access-mode decision that path exists to make.");

    /// <summary>
    /// Refuses a redirect as a typed, non-breaker answer. The client is built with
    /// <c>AllowAutoRedirect = false</c> (SEC-M1), so a 3xx reaches here as-is; without this check
    /// <c>EnsureSuccessStatusCode</c> would turn it into a generic <see cref="HttpRequestException"/>
    /// that the catch counted against the breaker, taking the whole source down. See
    /// <see cref="UpstreamRedirectRefusedException"/>. The whole 3xx range is refused, 304 included:
    /// the request sends no conditional headers, so a 304 cannot legitimately arise and is treated
    /// like any other non-payload answer (ADR 0014).
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
    /// SEC-M2: translates an upstream 429 (Too Many Requests) or 503 (Service Unavailable) into the
    /// designed <see cref="RequestLimitReachedException"/> signal before
    /// <c>EnsureSuccessStatusCode()</c> would otherwise turn it into a bare
    /// <see cref="HttpRequestException"/> — which surfaced a real upstream rate limit as an
    /// unhandled 500 on the proxy and a silently-empty search result instead of the
    /// Torznab/Newznab rate-limit element.
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
    /// The absolute endpoint for this indexer: <see cref="NewznabSourceOptions.ApiPath"/> resolved
    /// against the base URL. The leading slash is trimmed so the path is appended to the base rather
    /// than replacing its own path — an operator whose indexer lives behind a reverse proxy at
    /// <c>https://host/jackett/</c> configures the base once and is not silently rerouted to the
    /// host root.
    /// </summary>
    private Uri EndpointUri => new(_options.BaseUrl, _options.ApiPath.TrimStart('/'));

    /// <summary>
    /// Refuses an <see cref="NewznabSourceOptions.ApiPath"/> whose RESOLVED endpoint leaves the
    /// origin of <see cref="NewznabSourceOptions.BaseUrl"/>, because
    /// <see cref="AppendApiKey"/> puts the indexer's key in that endpoint's query string: an
    /// endpoint on another origin means the operator's key is handed to a host they never
    /// configured.
    ///
    /// <para><b>The trim in <see cref="EndpointUri"/> is NOT this defence, and assuming it is is
    /// exactly what produced the bug.</b> <c>TrimStart('/')</c> does neutralise the
    /// protocol-relative <c>//host/api</c> and <c>///host/api</c> forms, and <see cref="Uri"/>'s own
    /// normalisation flattens <c>../../api</c> back under the base — but
    /// <c>new Uri(base, relativeOrAbsolute)</c> REPLACES the base outright when the second argument
    /// parses as absolute, and a leading slash is not what makes it absolute. So
    /// <c>http://attacker.example/api</c> survives the trim untouched, as do its scheme-upgraded
    /// (<c>https://</c>), whitespace-prefixed (<see cref="Uri"/> strips leading whitespace),
    /// uppercase-scheme and scheme-downgraded (<c>file:///…</c>) variants.</para>
    ///
    /// <para><b>Asserted on the resolved endpoint, not on the ApiPath string.</b> Comparing scheme,
    /// host and port against the base closes every one of those variants under a single check,
    /// including ones nobody enumerated: there is no list of dangerous prefixes to keep current, and
    /// a form that resolves back onto the base origin is harmless by definition. A string-shape
    /// blacklist would have to be re-derived each time <see cref="Uri"/>'s parsing changes.</para>
    ///
    /// <para><b>Userinfo is part of the check even though it is not part of an origin.</b>
    /// <c>http://x@indexer.example:9117/api</c> matches the base on scheme, host AND port, so the
    /// three-way comparison alone accepts it — but the credentials ride into the request's
    /// authority, which is logged in full: the framework's URI redaction collapses only the QUERY
    /// string, and <c>LogMessageCleanser</c>'s patterns do not cover a userinfo segment. Requiring
    /// it to be empty keeps the authority exactly what the operator configured.</para>
    ///
    /// <para><b>The constructor, not <see cref="EndpointUri"/>'s getter</b>, so the source fails at
    /// CONSTRUCTION and can never be handed to a caller in a state where a later search would leak.
    /// That also covers the registry (arb-x7w8.4) as a second producer of
    /// <see cref="NewznabSourceOptions"/> without it having to know this rule exists.</para>
    ///
    /// <para><b>This is the configuration-time side of a three-sided origin invariant, and the three
    /// are not consolidatable.</b> Here the OUTBOUND endpoint is pinned to the configured base when
    /// the source is built; at response time the client refuses a redirect that would move the
    /// request off that origin (<c>AllowAutoRedirect = false</c>, ADR 0014); at parse time
    /// <c>TorznabFeedParser.TryValidateOriginPinnedLink</c> pins the links a feed hands back. They
    /// run at different times against different inputs — operator configuration, an upstream
    /// response status, and upstream-supplied feed content — so none of them can stand in for
    /// another, and a single shared check would have to be reached from all three.</para>
    ///
    /// <para>The exception carries <see cref="NewznabSourceOptions.SourceName"/> and nothing else.
    /// Rendering the offending endpoint or ApiPath would print an attacker-chosen host into the
    /// persistent log store at <c>/api/admin/logs</c>, and the ApiPath may itself be shaped to carry
    /// text there. See <see cref="SourceOriginRefusedException"/> for why it is deliberately NOT an
    /// <see cref="ArgumentException"/>.</para>
    /// </summary>
    /// <exception cref="SourceOriginRefusedException">The resolved endpoint is not on the base URL's origin.</exception>
    private void EnsureEndpointIsOnBaseOrigin()
    {
        var endpoint = EndpointUri;
        var baseUrl = _options.BaseUrl;

        var sameOrigin =
            string.Equals(endpoint.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(endpoint.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase)
            && endpoint.Port == baseUrl.Port
            && string.IsNullOrEmpty(endpoint.UserInfo);

        if (!sameOrigin)
        {
            throw new SourceOriginRefusedException(_options.SourceName);
        }
    }

    private static string SearchMode(SearchQuery query) => query switch
    {
        { Type: SearchType.TvSearch } => TvSearchMode,
        { Type: SearchType.Movie } => "movie",
        { TvdbId: not null } => TvSearchMode,
        { TmdbId: not null } => "movie",
        _ => "search",
    };

    /// <summary>The upstream <c>t=</c> value for a <c>tvsearch</c>, named once so the mode string
    /// the URI carries and the mode the id emission below branches on cannot drift.</summary>
    private const string TvSearchMode = "tvsearch";

    private Uri BuildSearchUri(SearchQuery query, int limit, int offset)
    {
        var mode = SearchMode(query);
        var builder = new UriBuilder(EndpointUri);
        var queryParams = new List<string>
        {
            "t=" + Uri.EscapeDataString(mode),
            "limit=" + limit.ToString(CultureInfo.InvariantCulture),
            "offset=" + offset.ToString(CultureInfo.InvariantCulture),
        };

        var queryText = query.QueryText?.Trim() ?? string.Empty;
        if (queryText.Length > 0)
        {
            queryParams.Add("q=" + Uri.EscapeDataString(queryText));
        }

        // Ids ride with the mode that accepts them, never folded into q: upstream matches an id
        // exactly, where the same digits inside the free-text term would just narrow the result set
        // for no reason. Season/ep are gated on the MODE rather than on the presence of a tvdbid, so
        // an id-less tvsearch still carries them.
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

        AppendApiKey(queryParams);

        builder.Query = string.Join("&", queryParams);
        return builder.Uri;
    }

    private Uri BuildCapsUri()
    {
        var builder = new UriBuilder(EndpointUri);
        var queryParams = new List<string> { "t=caps" };
        AppendApiKey(queryParams);
        builder.Query = string.Join("&", queryParams);
        return builder.Uri;
    }

    /// <summary>
    /// Appends the apikey as a QUERY-STRING parameter — never a header, never a path segment.
    ///
    /// <para><b>LOAD-BEARING (CLAUDE.md §1).</b> <c>IHttpClientFactory</c> attaches its own logging
    /// handler to every named client and logs the full absolute URI at Information, which since #65
    /// lands in the persistent log store served at <c>/api/admin/logs</c>. Two things make the query
    /// string the only safe place: the framework's own URI redaction collapses the whole query
    /// string to <c>?*</c> while printing every path segment in full, and
    /// <c>LogMessageCleanser</c> scrubs credentials in QUERY STRINGS only. A key moved into the path
    /// is covered by NEITHER and lands in the log store verbatim. A key moved into a header escapes
    /// the URI log, but then rides on every request this client makes including any future
    /// non-indexer one, and is not scrubbed from an exception's own rendering of the request.</para>
    ///
    /// <para>Centralised in one method rather than repeated at each call site so that the two
    /// builders cannot disagree about placement, and so the placement test has a single seam to
    /// pin.</para>
    /// </summary>
    private void AppendApiKey(List<string> queryParams) =>
        queryParams.Add("apikey=" + Uri.EscapeDataString(_options.ApiKey));
}
