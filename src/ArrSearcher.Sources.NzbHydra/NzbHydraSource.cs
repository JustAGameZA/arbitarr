using System.Globalization;
using System.Xml.Linq;
using ArrSearcher.Core.Releases;
using ArrSearcher.Core.Sources;
using ArrSearcher.Core.Sources.CircuitBreaker;

namespace ArrSearcher.Sources.NzbHydra;

/// <summary>
/// <see cref="IUpstreamSource"/> implementation backed by a real NZBHydra2 instance's
/// Torznab-compatible <c>/torznab/api</c> (search) and <c>/api</c> (caps) endpoints.
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
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                page = ParseTorznabResponse(body);
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

    public async Task<SourceCaps> GetCapsAsync(CancellationToken cancellationToken = default)
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

        if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Circuit breaker for source '{Name}' is open; refusing to call upstream.");
        }

        try
        {
            await _rateLimiter.WaitForTokenAsync(cancellationToken).ConfigureAwait(false);

            var response = await _httpClient.GetAsync(release.Link, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _circuitBreaker.RecordFailureAsync(Name, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private Uri BuildSearchUri(SearchQuery query, int limit, int offset)
    {
        var builder = new UriBuilder(new Uri(_options.BaseUrl, "torznab/api"));
        var queryParams = new List<string>
        {
            "t=" + Uri.EscapeDataString(query.Categories.Count > 0 ? "search" : "search"),
            "limit=" + limit.ToString(CultureInfo.InvariantCulture),
            "offset=" + offset.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(query.QueryText))
        {
            queryParams.Add("q=" + Uri.EscapeDataString(query.QueryText));
        }

        if (query.Categories.Count > 0)
        {
            queryParams.Add("cat=" + Uri.EscapeDataString(string.Join(",", query.Categories)));
        }

        // apikey is passed as a query-string parameter, never a header, never logged.
        queryParams.Add("apikey=" + Uri.EscapeDataString(_options.ApiKey));

        builder.Query = string.Join("&", queryParams);
        return builder.Uri;
    }

    private Uri BuildCapsUri()
    {
        var builder = new UriBuilder(new Uri(_options.BaseUrl, "torznab/api"));
        builder.Query = "t=caps&apikey=" + Uri.EscapeDataString(_options.ApiKey);
        return builder.Uri;
    }

    private static List<ReleaseCandidate> ParseTorznabResponse(string xml)
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

            var pubDate = TryParseDate(pubDateRaw) ?? DateTimeOffset.UtcNow;
            var linkUri = Uri.TryCreate(link, UriKind.Absolute, out var parsedLink)
                ? parsedLink
                : new Uri("about:blank");

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

        var categoryIds = doc.Descendants("category")
            .Select(c => c.Attribute("id")?.Value)
            .Where(v => v is not null)
            .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .ToArray();

        var searchingElement = doc.Descendants("searching").FirstOrDefault();
        var supportsTv = string.Equals(
            searchingElement?.Element("tv-search")?.Attribute("available")?.Value,
            "yes",
            StringComparison.OrdinalIgnoreCase);
        var supportsMovie = string.Equals(
            searchingElement?.Element("movie-search")?.Attribute("available")?.Value,
            "yes",
            StringComparison.OrdinalIgnoreCase);

        var limitsElement = doc.Descendants("limits").FirstOrDefault();
        int? maxPageSize = null;
        if (limitsElement is not null
            && int.TryParse(limitsElement.Attribute("max")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
        {
            maxPageSize = max;
        }

        return new SourceCaps(categoryIds, supportsTv, supportsMovie, maxPageSize);
    }
}
