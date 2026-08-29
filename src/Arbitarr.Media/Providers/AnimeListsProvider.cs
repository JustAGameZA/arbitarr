using System.Xml.Linq;

namespace Arbitarr.Media.Providers;

/// <summary>
/// Client for AniDB's <c>anime-lists</c> static XML identity mapping (Q5-D preference order: third
/// and last, after <see cref="ArrApiProvider"/> and <see cref="XemProvider"/>).
/// </summary>
/// <remarks>
/// <para>
/// The mapping document is fetched at runtime into a configurable local directory (default
/// <c>/config</c>) and never vendored into the repository (AC21): the source XML changes over time
/// with no changelog (plan R7), so shipping a copy in-repo would silently drift from upstream.
/// </para>
/// <para>
/// AC19 (AniDB-related fetch etiquette) is enforced in two independent ways, both anchored on the
/// persisted file rather than a separate tracking store:
/// <list type="bullet">
/// <item><description><b>Rate limit (≤1 request / 2s)</b>: an in-process monotonic-clock gate
/// (<see cref="_lastRequestAt"/>) delays any fetch that would otherwise follow the previous one too
/// closely. This covers repeated calls within a single process lifetime.</description></item>
/// <item><description><b>No re-fetch within 24h</b>: before issuing an HTTP request at all, the
/// provider checks the persisted file's <see cref="File.GetLastWriteTimeUtc(string)"/> timestamp in
/// the config directory. If it is fresher than <see cref="AnimeListsProviderOptions.EffectiveMinimumRefetchInterval"/>,
/// the on-disk copy is used as-is with no network call — this is what "coordinate the freshness check
/// with the file timestamp in /config, no extra store needed" means: the filesystem mtime *is* the
/// freshness record, surviving process restarts without any additional persistence.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class AnimeListsProvider
{
    private const string SourceName = "AnimeLists";

    private readonly AnimeListsProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private DateTimeOffset? _lastRequestAt;
    private AnimeListsDataset? _cachedDataset;

    public AnimeListsProvider(AnimeListsProviderOptions options, HttpClient httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        _httpClient.Timeout = options.EffectiveRequestTimeout;
    }

    public string Name => SourceName;

    /// <summary>
    /// Looks up the AniDB anime-lists entry for a series by AniDB id, ensuring the local dataset is
    /// present and fresh enough per AC19 before searching it.
    /// </summary>
    public async Task<AnimeListsResult<AnimeListsEntry>> GetByAniDbIdAsync(
        int aniDbId,
        CancellationToken cancellationToken = default)
    {
        var dataset = await EnsureDatasetAsync(cancellationToken).ConfigureAwait(false);
        if (dataset is null)
        {
            return AnimeListsResult<AnimeListsEntry>.Unreachable();
        }

        var entry = dataset.Entries.FirstOrDefault(e => e.AniDbId == aniDbId);
        return entry is null
            ? AnimeListsResult<AnimeListsEntry>.NoCoverage()
            : AnimeListsResult<AnimeListsEntry>.Success(entry);
    }

    /// <summary>
    /// Looks up the AniDB anime-lists entry for a series by TVDB id, ensuring the local dataset is
    /// present and fresh enough per AC19 before searching it.
    /// </summary>
    public async Task<AnimeListsResult<AnimeListsEntry>> GetByTvdbIdAsync(
        int tvdbId,
        CancellationToken cancellationToken = default)
    {
        var dataset = await EnsureDatasetAsync(cancellationToken).ConfigureAwait(false);
        if (dataset is null)
        {
            return AnimeListsResult<AnimeListsEntry>.Unreachable();
        }

        var entry = dataset.Entries.FirstOrDefault(e => e.TvdbId == tvdbId);
        return entry is null
            ? AnimeListsResult<AnimeListsEntry>.NoCoverage()
            : AnimeListsResult<AnimeListsEntry>.Success(entry);
    }

    /// <summary>
    /// Ensures the anime-lists dataset is loaded: reuses the in-memory copy if already parsed this
    /// process lifetime AND the on-disk file has not gone stale since, otherwise reads the on-disk
    /// copy in <c>/config</c> if it is fresh enough per AC19, otherwise fetches a new copy at runtime
    /// (never from a vendored/embedded resource, AC21) and persists it back to <c>/config</c>. The
    /// in-memory cache is re-validated against the file's mtime on every call so a long-lived instance
    /// still honors the 24h re-fetch rule rather than serving its first-ever load forever.
    /// </summary>
    private async Task<AnimeListsDataset?> EnsureDatasetAsync(CancellationToken cancellationToken)
    {
        var path = _options.FilePath;

        if (_cachedDataset is not null && !IsStale(path))
        {
            return _cachedDataset;
        }

        if (File.Exists(path) && !IsStale(path))
        {
            var onDisk = await TryParseFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (onDisk is not null)
            {
                _cachedDataset = onDisk;
                return onDisk;
            }
        }

        var fetched = await FetchAndPersistAsync(cancellationToken).ConfigureAwait(false);
        if (fetched is not null)
        {
            _cachedDataset = fetched;
            return fetched;
        }

        // Runtime fetch failed (or was skipped by rate limiting with nothing yet on disk); fall back
        // to whatever stale copy exists in /config rather than reporting unreachable outright, since a
        // hand-edited, rarely-changing dataset that is a day old is still far better than nothing.
        if (File.Exists(path))
        {
            return await TryParseFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private bool IsStale(string path)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path);
        return DateTime.UtcNow - lastWrite >= _options.EffectiveMinimumRefetchInterval;
    }

    private async Task<AnimeListsDataset?> FetchAndPersistAsync(CancellationToken cancellationToken)
    {
        await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check staleness/freshness now that we hold the gate: another caller may have just
            // fetched while we were waiting.
            var path = _options.FilePath;
            if (File.Exists(path) && !IsStale(path))
            {
                return await TryParseFileAsync(path, cancellationToken).ConfigureAwait(false);
            }

            await ApplyRateLimitAsync(cancellationToken).ConfigureAwait(false);

            string body;
            try
            {
                using var response = await _httpClient.GetAsync(_options.SourceUrl, cancellationToken).ConfigureAwait(false);
                _lastRequestAt = DateTimeOffset.UtcNow;

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastRequestAt = DateTimeOffset.UtcNow;
                return null;
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            var dataset = ParseXml(body);

            Directory.CreateDirectory(_options.ConfigDirectory);
            await File.WriteAllTextAsync(path, body, cancellationToken).ConfigureAwait(false);

            return dataset;
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    /// <summary>
    /// AC19 rate limit: at most one request per <see cref="AnimeListsProviderOptions.EffectiveMinimumRequestSpacing"/>.
    /// Delays the caller rather than dropping the request, since anime-lists fetches are infrequent
    /// (gated separately by the 24h re-fetch rule) and a short wait is preferable to surfacing a
    /// spurious "unreachable" result.
    /// </summary>
    private async Task ApplyRateLimitAsync(CancellationToken cancellationToken)
    {
        if (_lastRequestAt is not { } lastRequestAt)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - lastRequestAt;
        var minSpacing = _options.EffectiveMinimumRequestSpacing;
        if (elapsed < minSpacing)
        {
            await Task.Delay(minSpacing - elapsed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<AnimeListsDataset?> TryParseFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content) ? null : ParseXml(content);
        }
        catch (IOException)
        {
            return null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses AniDB's anime-lists XML shape: a root <c>&lt;anime-list&gt;</c> containing
    /// <c>&lt;anime anidbid="..." tvdbid="..." defaulttvdbseason="..."&gt;</c> elements, each
    /// optionally containing <c>&lt;name&gt;</c> child elements for alternate titles.
    /// </summary>
    private static AnimeListsDataset ParseXml(string xml)
    {
        var doc = XDocument.Parse(xml);
        var entries = new List<AnimeListsEntry>();

        foreach (var animeElement in doc.Descendants("anime"))
        {
            var aniDbId = GetIntAttribute(animeElement, "anidbid");
            if (aniDbId is null)
            {
                continue;
            }

            var tvdbId = GetIntAttribute(animeElement, "tvdbid");
            var tmdbId = GetIntAttribute(animeElement, "tmdbid");
            var defaultSeason = GetIntAttribute(animeElement, "defaulttvdbseason");

            var names = animeElement.Elements("name")
                .Select(n => n.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            entries.Add(new AnimeListsEntry(aniDbId.Value, tvdbId, tmdbId, defaultSeason, names));
        }

        return new AnimeListsDataset(entries);
    }

    private static int? GetIntAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}
