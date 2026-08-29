using System.Globalization;
using System.Text.Json;
using ArrSearcher.Core.Identity;
using ArrSearcher.Core.Sources.CircuitBreaker;

namespace ArrSearcher.Media.Providers;

/// <summary>
/// One row of TheXEM's <c>map/all</c> scene-numbering table: a single episode's numbering as seen
/// from one origin (e.g. <c>tvdb</c>) alongside its <c>scene</c> counterpart.
/// </summary>
/// <param name="Season">Season number under this row's origin.</param>
/// <param name="Episode">Episode number under this row's origin.</param>
/// <param name="Absolute">Absolute episode number, if XEM supplies one for this row.</param>
public sealed record XemNumberingRow(int Season, int Episode, int? Absolute);

/// <summary>
/// A single origin/scene numbering pair from <c>map/all</c> — the release-facing "scene" numbering
/// and the corresponding numbering under some other origin (e.g. TheTVDB), for one episode.
/// </summary>
/// <param name="Scene">The scene (release-group-facing) numbering.</param>
/// <param name="Origin">The other origin's numbering for the same episode (e.g. TVDB), when present.</param>
public sealed record XemNumberingPair(XemNumberingRow Scene, XemNumberingRow? Origin);

/// <summary>
/// The full <c>map/all</c> numbering table for one series: every scene/origin numbering pair XEM
/// knows about, unfiltered and unranked (ranking is Step 3b's concern).
/// </summary>
/// <param name="Pairs">All numbering pairs XEM has recorded for the series.</param>
public sealed record XemAllMap(IReadOnlyList<XemNumberingPair> Pairs);

/// <summary>
/// XEM's season-keyed arc-title map from <c>map/names?origin=tvdb&amp;id=</c>: for each TVDB season
/// number, the set of alternate titles release groups use for episodes drawn from that season/arc.
/// </summary>
/// <remarks>
/// This is the map that resolves the Bleach example: release <c>Bleach-17x36(402)</c> carries the arc
/// title "Thousand-Year Blood War", which this map associates with a specific TVDB season — never
/// flattened into a single global alternate-name list, because the same arc title can legitimately
/// repeat across unrelated series, and because collapsing away the season key is exactly what would
/// make the arc-relative vs. original-run collision unresolvable.
/// </remarks>
/// <param name="TitlesBySeason">TVDB season number to the alternate titles recorded for that season.</param>
public sealed record XemSeasonNamesMap(IReadOnlyDictionary<int, IReadOnlyList<string>> TitlesBySeason);

/// <summary>
/// Keyless client for thexem.info's <c>map/*</c> endpoints (Q5-D preference order: second, after
/// <see cref="ArrApiProvider"/>).
/// </summary>
/// <remarks>
/// <para>
/// Covers all four endpoints used by identity resolution: <c>map/havemap</c> (coverage check),
/// <c>map/all?id=</c> (full scene/origin numbering table), <c>map/allNames</c> (flat alternate-name
/// list), and critically <c>map/names?origin=tvdb&amp;id=</c> — the season-keyed arc-title map that
/// resolves the Bleach example (release <c>Bleach-17x36(402)</c> mapping to arc-relative S01E36 of
/// the TYBW arc). No API key is required; this is a public, keyless third-party service.
/// </para>
/// <para>
/// Every method returns <see cref="XemResult{T}"/> rather than a bare nullable payload so that AC-M6's
/// two distinct XEM-side degraded states — source-unreachable vs. no-xem-coverage — are always
/// distinguishable by the caller, never collapsed into a single null.
/// </para>
/// </remarks>
public sealed class XemProvider
{
    private const string SourceName = "Xem";

    private readonly XemProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IAsyncCircuitBreaker _circuitBreaker;

    public XemProvider(
        XemProviderOptions options,
        HttpClient httpClient,
        IAsyncCircuitBreaker circuitBreaker)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));

        _httpClient.Timeout = options.EffectiveRequestTimeout;
    }

    public string Name => SourceName;

    /// <summary>
    /// Calls <c>map/havemap?origin=&amp;id=</c> to check whether XEM has any coverage at all for a
    /// series under the given origin, before spending a second request on <c>map/all</c>/<c>map/names</c>.
    /// </summary>
    /// <param name="origin">The origin id space the caller's id is expressed in, e.g. "tvdb".</param>
    /// <param name="id">The series id in that origin's id space.</param>
    public async Task<XemResult<bool>> HaveMapAsync(string origin, int id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var uri = BuildUri("map/havemap", ("origin", origin), ("id", id.ToString(CultureInfo.InvariantCulture)));
        var response = await FetchJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return XemResult<bool>.Unreachable();
        }

        // XEM's envelope: { "result": "success", "data": true|false }
        if (!TryGetSuccessData(response.Value, out var data))
        {
            return XemResult<bool>.Unreachable();
        }

        var haveMap = data.ValueKind == JsonValueKind.True;
        return haveMap ? XemResult<bool>.Success(true) : XemResult<bool>.NoCoverage();
    }

    /// <summary>
    /// Calls <c>map/all?id=</c> to fetch the full scene/origin numbering table for a series.
    /// </summary>
    /// <param name="id">The series id (scene/TVDB id space XEM keys <c>map/all</c> by).</param>
    public async Task<XemResult<XemAllMap>> GetAllAsync(int id, CancellationToken cancellationToken = default)
    {
        var uri = BuildUri("map/all", ("id", id.ToString(CultureInfo.InvariantCulture)));
        var response = await FetchJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return XemResult<XemAllMap>.Unreachable();
        }

        if (!TryGetSuccessData(response.Value, out var data))
        {
            return XemResult<XemAllMap>.Unreachable();
        }

        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return XemResult<XemAllMap>.NoCoverage();
        }

        var pairs = new List<XemNumberingPair>();
        foreach (var element in data.EnumerateArray())
        {
            var scene = ReadRow(element, "scene");
            var origin = ReadRow(element, "tvdb") ?? ReadRow(element, "tmdb") ?? ReadRow(element, "anidb");
            if (scene is not null)
            {
                pairs.Add(new XemNumberingPair(scene, origin));
            }
        }

        return pairs.Count == 0
            ? XemResult<XemAllMap>.NoCoverage()
            : XemResult<XemAllMap>.Success(new XemAllMap(pairs));
    }

    /// <summary>
    /// Calls <c>map/allNames</c> to fetch the flat set of alternate names XEM has recorded for a
    /// series, with no season/arc keying.
    /// </summary>
    /// <param name="id">The series id.</param>
    /// <param name="origin">Optional origin id space; when omitted XEM defaults to its own scene ids.</param>
    public async Task<XemResult<IReadOnlyList<string>>> GetAllNamesAsync(
        int id,
        string? origin = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<(string, string)> { ("id", id.ToString(CultureInfo.InvariantCulture)) };
        if (!string.IsNullOrWhiteSpace(origin))
        {
            parameters.Add(("origin", origin));
        }

        var uri = BuildUri("map/allNames", parameters.ToArray());
        var response = await FetchJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return XemResult<IReadOnlyList<string>>.Unreachable();
        }

        if (!TryGetSuccessData(response.Value, out var data))
        {
            return XemResult<IReadOnlyList<string>>.Unreachable();
        }

        var names = ReadFlatNameArray(data);
        return names.Count == 0
            ? XemResult<IReadOnlyList<string>>.NoCoverage()
            : XemResult<IReadOnlyList<string>>.Success(names);
    }

    /// <summary>
    /// Calls <c>map/names?origin=tvdb&amp;id=</c> — the season-keyed arc-title map. Critically, the
    /// per-season keying returned by XEM is preserved in <see cref="XemSeasonNamesMap"/> rather than
    /// flattened, because it is exactly that keying which resolves the Bleach example: an arc title
    /// only identifies the correct season/arc when it stays associated with the season XEM recorded
    /// it under.
    /// </summary>
    /// <param name="origin">The origin id space the caller's id is expressed in, e.g. "tvdb".</param>
    /// <param name="id">The series id in that origin's id space.</param>
    public async Task<XemResult<XemSeasonNamesMap>> GetNamesAsync(
        string origin,
        int id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        var uri = BuildUri("map/names", ("origin", origin), ("id", id.ToString(CultureInfo.InvariantCulture)));
        var response = await FetchJsonAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return XemResult<XemSeasonNamesMap>.Unreachable();
        }

        if (!TryGetSuccessData(response.Value, out var data))
        {
            return XemResult<XemSeasonNamesMap>.Unreachable();
        }

        var bySeason = new Dictionary<int, IReadOnlyList<string>>();

        // XEM's map/names data shape is an object keyed by season number (as a string), each value
        // an array of alternate-title strings for that season's arc.
        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in data.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var season))
                {
                    continue;
                }

                var titles = ReadFlatNameArray(property.Value);
                if (titles.Count > 0)
                {
                    bySeason[season] = titles;
                }
            }
        }

        return bySeason.Count == 0
            ? XemResult<XemSeasonNamesMap>.NoCoverage()
            : XemResult<XemSeasonNamesMap>.Success(new XemSeasonNamesMap(bySeason));
    }

    private static List<string> ReadFlatNameArray(JsonElement element)
    {
        var names = new List<string>();
        if (element.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    names.Add(value);
                }
            }
        }

        return names;
    }

    private static XemNumberingRow? ReadRow(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var row) || row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var season = row.TryGetProperty("season", out var seasonProp) && seasonProp.TryGetInt32(out var s) ? s : 0;
        var episode = row.TryGetProperty("episode", out var episodeProp) && episodeProp.TryGetInt32(out var e) ? e : 0;
        int? absolute = row.TryGetProperty("absolute", out var absProp) && absProp.TryGetInt32(out var abs) ? abs : null;

        return new XemNumberingRow(season, episode, absolute);
    }

    private static bool TryGetSuccessData(JsonElement root, out JsonElement data)
    {
        data = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("result", out var resultProp)
            && resultProp.ValueKind == JsonValueKind.String
            && !string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!root.TryGetProperty("data", out data))
        {
            return false;
        }

        return true;
    }

    private Uri BuildUri(string path, params (string Key, string Value)[] query)
    {
        var builder = new UriBuilder(new Uri(_options.BaseUrl, path));
        builder.Query = string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        return builder.Uri;
    }

    private async Task<JsonElement?> FetchJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await _circuitBreaker.RecordFailureAsync(
                    Name,
                    new HttpRequestException($"XEM {uri.AbsolutePath} returned {(int)response.StatusCode}"),
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                await _circuitBreaker.RecordFailureAsync(
                    Name,
                    new InvalidOperationException($"XEM {uri.AbsolutePath} returned an empty body"),
                    cancellationToken).ConfigureAwait(false);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _circuitBreaker.RecordFailureAsync(Name, ex, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }
}
