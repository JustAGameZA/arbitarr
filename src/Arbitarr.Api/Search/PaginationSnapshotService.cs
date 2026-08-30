using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Search;

/// <summary>
/// Materializes a merged query's full result set into a <see cref="IQuerySnapshotStore"/>-backed
/// snapshot keyed deterministically by the query's identity — search type (<c>t</c>), query text
/// (<c>q</c>), and categories (<c>cat</c>) — explicitly excluding <c>offset</c>/<c>limit</c>, so
/// that <c>offset=0&amp;limit=50</c> and a subsequent <c>offset=50&amp;limit=50</c> against "the
/// same query" are served disjoint, union-complete slices of the same materialized set (M1-5,
/// AC16), even if the upstream source set mutates between the two calls.
///
/// <para>
/// On a snapshot miss, the full result set is resolved via <see cref="SearchResultCacheStage"/>
/// (Step 4a's two-age cache) rather than calling <see cref="UpstreamMergeStage"/> directly: a
/// fresh/stale-but-valid two-age cache entry is served with zero upstream calls (stale also
/// triggers the refresh worker's pickup), and only a miss/expired entry actually fans out via
/// <see cref="UpstreamMergeStage"/>. The two caches are complementary, not competing — this
/// snapshot layer provides 60-300s page stability (AC16/M1-5) over whatever result set the
/// two-age cache is currently serving (fresh/serve ages of 15 min/7 d); the snapshot's own TTL
/// is always far shorter than the two-age cache's <c>fresh_until</c>, so re-materializing a
/// snapshot from an unchanged two-age entry is coherent, not stale-on-stale.
/// </para>
///
/// <para>
/// This applies uniformly to every request: an id-based request (tvdbid/tmdbid + season/ep
/// present) keys the two-age cache on the resolved <see cref="SeriesIdentity"/>/
/// <see cref="NumberingCandidate"/> (M3-9's S17E36/17x36 collapse); a <c>q</c>-only request falls
/// back to a title-set identity built from the raw query text (no <c>IEpisodeMatcher</c>/
/// <c>IIdentityResolver</c> exists yet — out of scope, M5/M6 territory) so it still round-trips
/// through the same two-age cache and carries the same age/band provenance.
/// </para>
///
/// <para>
/// The set-level <c>Age</c>/<c>Band</c> the two-age cache stage returns is persisted inside the
/// snapshot payload itself (see <see cref="SnapshotPayload"/>), so a page-2 request sliced from an
/// already-materialized snapshot reports the same provenance as page 1 did, without re-touching
/// the two-age cache.
/// </para>
/// </summary>
public sealed class PaginationSnapshotService
{
    /// <summary>
    /// Default query-snapshot TTL (300s), matching <c>SettingKey.QuerySnapshotTtl</c>'s documented
    /// default. Used as the fallback inside <see cref="StaticSnapshotTtlSource"/> when a caller
    /// (tests, or a future non-Host caller) does not supply an explicit <paramref name="ttl"/> nor
    /// an <see cref="ISnapshotTtlSource"/> — see the two-ctor split below (M7-8c/AC24).
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);

    private readonly UpstreamMergeStage _mergeStage;
    private readonly SearchResultCacheStage _cacheStage;
    private readonly IQuerySnapshotStore _snapshotStore;
    private readonly TimeProvider _timeProvider;
    private readonly ISnapshotTtlSource _ttlSource;

    /// <summary>
    /// Fixed-TTL constructor: wraps <paramref name="ttl"/> (or <see cref="DefaultTtl"/> when null)
    /// in a <see cref="StaticSnapshotTtlSource"/>. Used directly by tests and by any caller that
    /// wants a TTL fixed for the service's lifetime — existing call sites and their semantics are
    /// unchanged.
    /// </summary>
    public PaginationSnapshotService(
        UpstreamMergeStage mergeStage,
        SearchResultCacheStage cacheStage,
        IQuerySnapshotStore snapshotStore,
        TimeProvider timeProvider,
        TimeSpan? ttl = null)
        : this(mergeStage, cacheStage, snapshotStore, timeProvider, new StaticSnapshotTtlSource(ttl ?? DefaultTtl))
    {
    }

    /// <summary>
    /// Live-TTL constructor (M7-8c/AC24): reads the snapshot TTL from <paramref name="ttlSource"/>
    /// on every <see cref="GetAsync"/> call instead of capturing a fixed value at construction, so
    /// a <c>QuerySnapshotTtl</c> setting changed via the admin API takes effect on the very next
    /// request. This is the ctor the Host wires up (see <c>Program.cs</c>); DI resolves
    /// <see cref="ISnapshotTtlSource"/> from the same per-request scope as everything else this
    /// service depends on.
    /// </summary>
    public PaginationSnapshotService(
        UpstreamMergeStage mergeStage,
        SearchResultCacheStage cacheStage,
        IQuerySnapshotStore snapshotStore,
        TimeProvider timeProvider,
        ISnapshotTtlSource ttlSource)
    {
        _mergeStage = mergeStage ?? throw new ArgumentNullException(nameof(mergeStage));
        _cacheStage = cacheStage ?? throw new ArgumentNullException(nameof(cacheStage));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ttlSource = ttlSource ?? throw new ArgumentNullException(nameof(ttlSource));
    }

    /// <summary>
    /// Resolves the full, order-stable merged release set for the query identity carried by
    /// <paramref name="query"/> (ignoring its Offset/Limit), from a live snapshot if one exists,
    /// or by materializing a fresh one via <see cref="SearchResultCacheStage"/> otherwise. Also
    /// returns the set of rate-limited source names from a fresh merge (empty on a snapshot hit,
    /// since a snapshot hit performs no upstream calls), plus the set-level cache Age/Band every
    /// served response must carry (AC-M7a-cache).
    /// </summary>
    public async Task<PagedMergeResult> GetPageAsync(
        string searchType,
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var snapshotToken = ComputeSnapshotToken(searchType, query.QueryText, query.Categories);
        var now = _timeProvider.GetUtcNow();

        var cached = await _snapshotStore.GetAsync(snapshotToken, now, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var payload = JsonSerializer.Deserialize<SnapshotPayload>(cached) ?? SnapshotPayload.Empty;
            return new PagedMergeResult(Slice(payload.Releases, query.Offset, query.Limit), Array.Empty<string>(), payload.Age, payload.Band);
        }

        var rateLimitedSources = new List<string>();

        // No inline refresh trigger is supplied: a stale-but-valid read stamps LastRequestedAt,
        // which is what places the entry inside the RefreshWorker's active_window selection, so the
        // worker performs the refresh off the request path (D1, M3-11).
        var stageResult = await _cacheStage.GetAsync(
            query,
            async ct =>
            {
                var merged = await _mergeStage.MergeAsync(query, ct).ConfigureAwait(false);
                rateLimitedSources.AddRange(merged.RateLimitedSources);
                return new UpstreamFetchResult(merged.Releases, Degraded: merged.RateLimitedSources.Count > 0);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Only persist a snapshot when the resolved set actually has results or every source is
        // healthy-but-empty; a fully rate-limited fresh merge should not be cached, so the next
        // request gets a fresh chance once sources recover. A cache-served set (no fresh merge
        // performed) always persists — it carries no rate-limit signal to withhold on.
        if (stageResult.Releases.Count > 0 || rateLimitedSources.Count == 0)
        {
            var ttl = await _ttlSource.GetAsync(cancellationToken).ConfigureAwait(false);
            var payload = new SnapshotPayload(stageResult.Releases, stageResult.Age, stageResult.Band);
            var payloadJson = JsonSerializer.Serialize(payload);
            await _snapshotStore.SaveAsync(snapshotToken, payloadJson, now, ttl, cancellationToken).ConfigureAwait(false);
        }

        return new PagedMergeResult(Slice(stageResult.Releases, query.Offset, query.Limit), rateLimitedSources, stageResult.Age, stageResult.Band);
    }

    private static IReadOnlyList<RenderedRelease> Slice(IReadOnlyList<RenderedRelease> releases, int offset, int limit)
    {
        if (offset >= releases.Count || limit <= 0)
        {
            return Array.Empty<RenderedRelease>();
        }

        var take = Math.Min(limit, releases.Count - offset);
        return releases.Skip(offset).Take(take).ToArray();
    }

    /// <summary>
    /// Deterministic snapshot key derived only from the query's identity-defining parameters —
    /// never <c>offset</c>/<c>limit</c>, since those are what legitimately varies between two
    /// pages of "the same query" and must resolve to the same snapshot row.
    /// </summary>
    private static string ComputeSnapshotToken(string searchType, string? queryText, IReadOnlyList<int> categories)
    {
        var normalizedCategories = string.Join(",", categories.OrderBy(c => c));
        var raw = $"{searchType}{queryText}{normalizedCategories}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// A single requested page (offset/limit slice) of a snapshot, plus any sources rate-limited
/// while materializing it, plus the set-level two-age cache provenance (AC-M7a-cache) every
/// served response must carry.
/// </summary>
public sealed record PagedMergeResult(
    IReadOnlyList<RenderedRelease> Releases,
    IReadOnlyList<string> RateLimitedSources,
    TimeSpan? CacheAge,
    CacheBand CacheBand);

/// <summary>
/// The pagination snapshot's persisted payload shape: the full merged release set plus the
/// two-age cache provenance it was resolved with, so a page-2 slice from an already-materialized
/// snapshot reports the same age/band page 1 did.
/// </summary>
public sealed record SnapshotPayload(IReadOnlyList<RenderedRelease> Releases, TimeSpan? Age, CacheBand Band)
{
    public static readonly SnapshotPayload Empty = new(Array.Empty<RenderedRelease>(), null, CacheBand.Expired);
}

/// <summary>
/// M7-8c/AC24: abstracts how <see cref="PaginationSnapshotService"/> obtains its snapshot TTL, so
/// the live implementation (over <c>SettingsRepository</c>/<c>SettingKey.QuerySnapshotTtl</c>) can
/// be read fresh on every <see cref="PaginationSnapshotService.GetAsync"/> call — a setting changed
/// through the admin API takes effect on the very next request, with no restart.
/// </summary>
public interface ISnapshotTtlSource
{
    ValueTask<TimeSpan> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Wraps a fixed TTL value for the lifetime of the owning <see cref="PaginationSnapshotService"/>.
/// Used by the service's fixed-TTL constructor (tests, and any caller that wants the old
/// capture-once-at-construction behavior) so existing call sites and tests keep their exact
/// semantics unaffected by the live-TTL path (M7-8c).
/// </summary>
public sealed class StaticSnapshotTtlSource(TimeSpan ttl) : ISnapshotTtlSource
{
    private readonly TimeSpan _ttl = ttl;

    public ValueTask<TimeSpan> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_ttl);
}
