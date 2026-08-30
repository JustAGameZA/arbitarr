using System.Text.Json;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Search;

/// <summary>
/// Request-path adapter between a <see cref="SearchQuery"/> and the two-age
/// <see cref="SearchResultCache"/> (plan Step 4a / M3). <b>Every</b> search request passes through
/// here — <see cref="PaginationSnapshotService"/> calls it at its snapshot-miss point — not only
/// id-carrying ones.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An id-based request (<c>tvdbid</c>/<c>tmdbid</c> + <c>season</c>/<c>ep</c>, the shape
/// Sonarr/Radarr actually send) is keyed on the resolved <see cref="SeriesIdentity"/> and
/// <see cref="NumberingCandidate"/> via <see cref="SearchCacheKeyBuilder"/>, so
/// <c>S17E36</c>/<c>17x36</c>/free-text spellings of the same episode collapse onto one entry
/// (AC23b(4), M3-9).</item>
/// <item>A <c>q</c>-only request has no provider id to key on and no
/// <c>IEpisodeMatcher</c>/<c>IIdentityResolver</c> exists yet (M5/M6), so it falls back to a
/// title-set identity built from its own query text — still deterministic, still cached, still
/// carrying age/band provenance; it simply does not collapse with other spellings yet.</item>
/// </list>
///
/// <para>Band behaviour is delegated to <see cref="SearchResultCache"/> (M3-8/M3-8a):</para>
/// <list type="bullet">
/// <item><b>Fresh</b> / <b>stale-but-valid</b>: the cached set is served with zero upstream calls.
/// A stale read additionally invokes <c>refreshTrigger</c>; the proactive
/// <see cref="RefreshWorker"/> is what actually re-fetches (it selects on the
/// <c>LastRequestedAt</c> stamp the read just made), so the inline path stays spared (D1, M3-11).</item>
/// <item><b>Expired</b> / miss: the cached set (if any) is <b>not served at all</b>. Upstream is
/// called via <c>upstreamFetch</c>; a successful fetch is stored and served as a
/// <see cref="CacheBand.Fresh"/> set of age zero. If the fetch is degraded (every source
/// rate-limited/failed, nothing returned) nothing is written — the sick path never overwrites data
/// (M3-10) — and the result is an empty set flagged <see cref="CacheBand.Expired"/>, which is the
/// plan's "flagged partial/empty result".</item>
/// </list>
///
/// <para>
/// The stored payload is <see cref="CachedSearchPayload"/>: the originating <see cref="SearchQuery"/>
/// plus the rendered releases. Persisting the query is what lets <see cref="SearchResultRefresher"/>
/// re-run it for the worker — the cache key is a one-way digest and cannot be inverted.
/// </para>
/// </remarks>
public sealed class SearchResultCacheStage
{
    /// <summary>Resolution-profile discriminator (M4 territory); fixed until profiles exist.</summary>
    /// <remarks>
    /// The cache is currently client-independent: every client shares one upstream credential set,
    /// so there is no per-client key to scope by yet. M4 must thread the resolved client/profile
    /// name (from <c>ClientKeyContext</c>) into <see cref="SearchCacheKeyBuilder.Build"/>'s
    /// <c>profile</c> parameter once per-client upstream keys or filter profiles exist — otherwise
    /// distinct clients continue to share cache entries.
    /// </remarks>
    public const string DefaultProfile = "default";

    private readonly SearchResultCache _cache;

    public SearchResultCacheStage(SearchResultCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    /// <summary>
    /// Derives a <see cref="SeriesIdentity"/> from request-level provider ids. Returns null when
    /// neither a TVDB nor a TMDB id is present — an imdbid-only request has no key the cache can use
    /// (imdbid is not part of <see cref="SeriesIdentity"/>/<see cref="SearchCacheKeyBuilder"/> today).
    /// </summary>
    public static SeriesIdentity? TryBuildIdentity(int? tvdbId, int? tmdbId, string? title) =>
        tvdbId is null && tmdbId is null
            ? null
            : new SeriesIdentity(tvdbId, tmdbId, title ?? string.Empty, Array.Empty<string>());

    /// <summary>
    /// Derives a <see cref="NumberingCandidate"/> directly from Sonarr/Radarr's own <c>season</c>/
    /// <c>ep</c> params — no free-text parsing. Uses <see cref="NumberingScheme.TvdbSeasonal"/> since
    /// these are already-resolved TVDB-seasonal numbers as supplied by the calling *arr app.
    /// </summary>
    public static NumberingCandidate BuildNumbering(int? season, int? episode) =>
        new(NumberingScheme.TvdbSeasonal, season, episode ?? 0, Absolute: null);

    /// <summary>Upper bound on the raw <c>q</c> text folded into the title-set fallback identity.</summary>
    /// <remarks>
    /// <see cref="SearchCacheKeyBuilder"/> already hashes its title-set token, so an unbounded
    /// <c>QueryText</c> cannot itself blow up the stored <c>QueryKey</c>. This cap exists one layer
    /// up regardless, so an unbounded request body is never carried into hashing (or into
    /// <see cref="SeriesIdentity"/>/<see cref="CachedSearchPayload"/>) in the first place.
    /// </remarks>
    private const int MaxFallbackQueryTextLength = 256;

    /// <summary>
    /// Resolves the two-age cache key for <paramref name="query"/>: provider-id identity when
    /// available (M3-9 collapse), title-set fallback otherwise. Categories are part of the key.
    /// </summary>
    public static string BuildQueryKey(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var identity = TryBuildIdentity(query.TvdbId, query.TmdbId, title: query.QueryText)
            ?? new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: BoundedFallbackText(query.QueryText), AlternateTitles: Array.Empty<string>());
        var candidate = BuildNumbering(query.Season, query.Episode);
        return SearchCacheKeyBuilder.Build(identity, candidate, query.Categories, DefaultProfile);
    }

    private static string BoundedFallbackText(string? queryText)
    {
        var trimmed = (queryText ?? string.Empty).Trim();
        return trimmed.Length > MaxFallbackQueryTextLength
            ? trimmed[..MaxFallbackQueryTextLength]
            : trimmed;
    }

    /// <summary>
    /// Reads the two-age cache for <paramref name="query"/>. On a fresh or stale-but-valid hit,
    /// returns the cached release set with zero upstream calls. On a miss or expired entry, invokes
    /// <paramref name="upstreamFetch"/>; stores and serves the result as fresh unless the fetch was
    /// degraded and empty, in which case nothing is stored and an empty, expired-flagged set is returned.
    /// </summary>
    public async Task<CacheStageResult> GetAsync(
        SearchQuery query,
        Func<CancellationToken, Task<UpstreamFetchResult>> upstreamFetch,
        Action? refreshTrigger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(upstreamFetch);

        var queryKey = BuildQueryKey(query);
        var read = await _cache.GetAsync(queryKey, refreshTrigger, cancellationToken).ConfigureAwait(false);

        if (read.IsServable && read.PayloadJson is not null)
        {
            var cachedReleases = CachedSearchPayload.Deserialize(read.PayloadJson)?.Releases ?? Array.Empty<RenderedRelease>();
            return new CacheStageResult(cachedReleases, read.Band, read.Age);
        }

        var fetched = await upstreamFetch(cancellationToken).ConfigureAwait(false);
        if (fetched.Releases.Count == 0 && fetched.Degraded)
        {
            // Sick upstream, nothing usable: leave whatever the store holds untouched (M3-10) and
            // flag the (empty) response as expired — nothing servable exists for this key right now.
            return new CacheStageResult(Array.Empty<RenderedRelease>(), CacheBand.Expired, null);
        }

        var payloadJson = new CachedSearchPayload(query, fetched.Releases).Serialize();
        await _cache.SaveAsync(
            queryKey,
            payloadJson,
            freshUntilAge: RefreshWorkerDefaults.FreshUntilAge,
            serveUntilAge: RefreshWorkerDefaults.ServeUntilAge,
            cancellationToken).ConfigureAwait(false);

        return new CacheStageResult(fetched.Releases, CacheBand.Fresh, TimeSpan.Zero);
    }
}

/// <summary>
/// Outcome of one inline upstream fetch handed to <see cref="SearchResultCacheStage.GetAsync"/>.
/// </summary>
/// <param name="Releases">Merged, rendered releases from every source that answered.</param>
/// <param name="Degraded">True when at least one source was rate-limited/failed, i.e. the set may be partial.</param>
public sealed record UpstreamFetchResult(IReadOnlyList<RenderedRelease> Releases, bool Degraded);

/// <summary>
/// Result of <see cref="SearchResultCacheStage.GetAsync"/>: the release set to render plus the
/// set-level provenance (age/band) every served set must carry (M3-5/AC-M7a-cache).
/// </summary>
/// <param name="Releases">The release set to render — cached, freshly fetched, or empty.</param>
/// <param name="Band">
/// <see cref="CacheBand.Fresh"/> for a fresh hit or a just-completed fetch, <see cref="CacheBand.StaleButValid"/>
/// for a stale hit, <see cref="CacheBand.Expired"/> only when nothing servable could be produced.
/// </param>
/// <param name="Age">Age of the served payload; <see cref="TimeSpan.Zero"/> for a just-completed fetch; null when nothing was served.</param>
public sealed record CacheStageResult(IReadOnlyList<RenderedRelease> Releases, CacheBand Band, TimeSpan? Age);

/// <summary>
/// Persisted shape of one two-age cache entry's payload: the query that produced it (so the
/// <see cref="RefreshWorker"/> can re-run it) and the rendered releases.
/// </summary>
public sealed record CachedSearchPayload(SearchQuery Query, IReadOnlyList<RenderedRelease> Releases)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    public static CachedSearchPayload? Deserialize(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<CachedSearchPayload>(payloadJson, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
