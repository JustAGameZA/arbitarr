using System.Globalization;
using System.Text.Json;
using ArrSearcher.Core.Identity;
using ArrSearcher.Core.Sources.CircuitBreaker;

namespace ArrSearcher.Media.Providers;

/// <summary>
/// The default, highest-priority identity resolver in the Q5-D preference order
/// (<c>ArrApiProvider</c> &gt; <c>XemProvider</c> &gt; <c>AnimeListsProvider</c>).
/// </summary>
/// <remarks>
/// <para>
/// Calls the *arr instance's own <c>/api/v3/episode</c> endpoint. Because Sonarr/Radarr have already
/// synced and reconciled their episode numbering from TheTVDB/TheMovieDB, a query that originates from
/// an *arr instance carries pre-reconciled season/episode data with no licensing or ambiguity problem —
/// unlike the XEM/AnimeLists path, which must fuzzily reconstruct that mapping from third-party,
/// hand-edited sources. When this provider returns a match, downstream identity resolution should
/// prefer it over the fuzzier providers.
/// </para>
/// <para>
/// This is a single authoritative lookup, not a paginated search: no fan-out, retries, or pagination
/// are introduced here, keeping this well within AC14's overall ≤12s response budget.
/// </para>
/// </remarks>
public sealed class ArrApiProvider : IIdentityResolver
{
    private const string SchemeName = "ArrApi";

    private readonly ArrApiProviderOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IAsyncCircuitBreaker _circuitBreaker;

    public ArrApiProvider(
        ArrApiProviderOptions options,
        HttpClient httpClient,
        IAsyncCircuitBreaker circuitBreaker)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));

        _httpClient.Timeout = options.EffectiveRequestTimeout;
    }

    public string Name => _options.SourceName;

    /// <summary>
    /// Resolves a canonical <see cref="SeriesIdentity"/> from the *arr instance's own episode database.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="IdentityResolutionHints.TvdbId"/> (the series ID *arr already tracks
    /// internally) since <c>/api/v3/episode</c> is keyed by series ID, not by free-text title search.
    /// Returns <see langword="null"/> when no TVDB hint is supplied, when the circuit breaker is open,
    /// or when *arr has no episodes on record for that series — callers should fall through to the
    /// next provider in the Q5-D preference order in all of those cases.
    /// </remarks>
    public async Task<SeriesIdentity?> ResolveAsync(
        string title,
        IdentityResolutionHints hints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hints);

        if (hints.TvdbId is not { } tvdbId)
        {
            // /api/v3/episode is keyed by *arr's internal series ID (sourced from TVDB); without it
            // there is nothing authoritative to look up, so defer to the next provider.
            return null;
        }

        var result = await FetchEpisodesAsync(tvdbId, cancellationToken).ConfigureAwait(false);
        if (result.Kind != ArrApiOutcomeKind.Success || result.Value is not { Count: > 0 } episodes)
        {
            return null;
        }

        var primaryTitle = episodes[0].SeriesTitle ?? title;
        return new SeriesIdentity(tvdbId, TmdbId: null, primaryTitle, AlternateTitles: Array.Empty<string>());
    }

    /// <summary>
    /// Resolves the pre-reconciled candidate numbering for a specific episode of a series *arr already
    /// tracks, together with provenance recording the "ArrApi" scheme at (or near) full confidence,
    /// since this endpoint is authoritative and requires no ambiguity resolution.
    /// </summary>
    /// <param name="tvdbId">The TVDB series ID *arr tracks internally.</param>
    /// <param name="season">Season number as *arr has it reconciled.</param>
    /// <param name="episode">Episode number as *arr has it reconciled.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// The candidate numbering set (a single, authoritative candidate) plus provenance carrying
    /// <see cref="IdentitySource.ArrApi"/>, or <see langword="null"/> if *arr is reachable but has no
    /// matching episode on record. When *arr cannot be reached at all, the returned provenance's
    /// <see cref="MatchProvenance.Flags"/> carries <see cref="MatchProvenanceFlags.SourceUnreachable"/>
    /// instead of this method returning <see langword="null"/> outright, so callers can distinguish
    /// "no such episode" from "could not ask" (AC-M6) before falling through to the next provider in
    /// the Q5-D preference order.
    /// </returns>
    public async Task<(CandidateNumberingSet Numbering, MatchProvenance Provenance)?> ResolveNumberingAsync(
        int tvdbId,
        int season,
        int episode,
        CancellationToken cancellationToken = default)
    {
        var result = await FetchEpisodesAsync(tvdbId, cancellationToken).ConfigureAwait(false);
        if (result.Kind == ArrApiOutcomeKind.Unreachable)
        {
            var unreachableProvenance = new MatchProvenance(
                SchemeName,
                Array.Empty<MatchEvidence>(),
                Confidence: 0.0,
                IdentitySource: IdentitySource.None,
                Flags: MatchProvenanceFlags.SourceUnreachable);

            return (new CandidateNumberingSet(Array.Empty<NumberingCandidate>()), unreachableProvenance);
        }

        var episodes = result.Value ?? Array.Empty<ArrEpisodeDto>();
        var match = episodes.FirstOrDefault(e => e.SeasonNumber == season && e.EpisodeNumber == episode);
        if (match is null)
        {
            return null;
        }

        var candidate = new NumberingCandidate(
            NumberingScheme.TvdbSeasonal,
            match.SeasonNumber,
            match.EpisodeNumber,
            Absolute: match.AbsoluteEpisodeNumber);

        var provenance = new MatchProvenance(
            SchemeName,
            new[]
            {
                new MatchEvidence(
                    $"*arr /api/v3/episode returned S{match.SeasonNumber:D2}E{match.EpisodeNumber:D2} directly for series {tvdbId}",
                    SchemeName),
            },
            Confidence: 1.0,
            IdentitySource: IdentitySource.ArrApi,
            Flags: MatchProvenanceFlags.None);

        return (new CandidateNumberingSet(new[] { candidate }), provenance);
    }

    private async Task<ArrApiResult<IReadOnlyList<ArrEpisodeDto>>> FetchEpisodesAsync(int tvdbSeriesId, CancellationToken cancellationToken)
    {
        if (!await _circuitBreaker.CanCallAsync(Name, cancellationToken).ConfigureAwait(false))
        {
            return ArrApiResult<IReadOnlyList<ArrEpisodeDto>>.Unreachable();
        }

        var uri = BuildEpisodeUri(tvdbSeriesId);

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await _circuitBreaker.RecordFailureAsync(
                    Name,
                    new HttpRequestException($"*arr /api/v3/episode returned {(int)response.StatusCode}"),
                    cancellationToken).ConfigureAwait(false);
                return ArrApiResult<IReadOnlyList<ArrEpisodeDto>>.Unreachable();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var episodes = ParseEpisodeResponse(body);
            await _circuitBreaker.RecordSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
            return ArrApiResult<IReadOnlyList<ArrEpisodeDto>>.Success(episodes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _circuitBreaker.RecordFailureAsync(Name, ex, cancellationToken).ConfigureAwait(false);
            return ArrApiResult<IReadOnlyList<ArrEpisodeDto>>.Unreachable();
        }
    }

    private Uri BuildEpisodeUri(int tvdbSeriesId)
    {
        var builder = new UriBuilder(new Uri(_options.BaseUrl, "api/v3/episode"));
        builder.Query = "seriesId=" + tvdbSeriesId.ToString(CultureInfo.InvariantCulture)
            + "&apikey=" + Uri.EscapeDataString(_options.ApiKey);
        return builder.Uri;
    }

    private static List<ArrEpisodeDto> ParseEpisodeResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<ArrEpisodeDto>();
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<ArrEpisodeDto>();
        }

        var results = new List<ArrEpisodeDto>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var seasonNumber = element.TryGetProperty("seasonNumber", out var seasonProp) && seasonProp.TryGetInt32(out var s)
                ? s
                : 0;
            var episodeNumber = element.TryGetProperty("episodeNumber", out var episodeProp) && episodeProp.TryGetInt32(out var e)
                ? e
                : 0;
            int? absoluteEpisodeNumber = element.TryGetProperty("absoluteEpisodeNumber", out var absProp) && absProp.TryGetInt32(out var abs)
                ? abs
                : null;
            string? seriesTitle = element.TryGetProperty("series", out var seriesProp)
                && seriesProp.ValueKind == JsonValueKind.Object
                && seriesProp.TryGetProperty("title", out var titleProp)
                && titleProp.ValueKind == JsonValueKind.String
                ? titleProp.GetString()
                : null;

            results.Add(new ArrEpisodeDto(seasonNumber, episodeNumber, absoluteEpisodeNumber, seriesTitle));
        }

        return results;
    }

    private sealed record ArrEpisodeDto(int SeasonNumber, int EpisodeNumber, int? AbsoluteEpisodeNumber, string? SeriesTitle);
}
