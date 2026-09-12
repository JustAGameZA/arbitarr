using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Pipeline;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Search;

/// <summary>
/// Materializes a merged query's full result set into a <see cref="IQuerySnapshotStore"/>-backed
/// snapshot keyed deterministically by the query's identity — search type (<c>t</c>), query text
/// (<c>q</c>), categories (<c>cat</c>), and provider/episode identifiers — explicitly excluding
/// <c>offset</c>/<c>limit</c>, so
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
    /// (tests, or a future non-Host caller) does not supply an explicit <c>ttl</c> nor
    /// an <see cref="ISnapshotTtlSource"/> — see the two-ctor split below (M7-8c/AC24).
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);

    private readonly UpstreamMergeStage _mergeStage;
    private readonly SearchResultCacheStage _cacheStage;
    private readonly DedupStage _dedupStage;
    private readonly IQuerySnapshotStore _snapshotStore;
    private readonly TimeProvider _timeProvider;
    private readonly ISnapshotTtlSource _ttlSource;
    private readonly ISourceSetFingerprintSource _sourceSetFingerprintSource;

    /// <summary>
    /// Fixed-TTL constructor: wraps <paramref name="ttl"/> (or <see cref="DefaultTtl"/> when null)
    /// in a <see cref="StaticSnapshotTtlSource"/>. Used directly by tests and by any caller that
    /// wants a TTL fixed for the service's lifetime — existing call sites and their semantics are
    /// unchanged.
    ///
    /// <para>arb-b5z: <paramref name="sourceSetFingerprintSource"/> defaults to
    /// <see cref="StaticSourceSetFingerprintSource.Empty"/>, a fixed empty fingerprint, so every
    /// pre-existing call site (all fourteen of them, across the golden/rendering/pagination test
    /// files) keeps compiling and behaving exactly as before — a caller that never told this type
    /// about a source set gets the pre-arb-b5z token shape verbatim.</para>
    ///
    /// <para>arb-x7w8.8: <paramref name="dedupStage"/> likewise defaults, to a stage over
    /// <see cref="AllEqualSourcePriority"/>. <b>Dedup is never skipped when it is omitted</b> —
    /// only the ORDERING within a group falls back to the source-name/position tiebreaks, which is
    /// the same ordering the Host itself produces until the source registry (arb-x7w8.4) supplies
    /// real priorities. Defaulting it rather than requiring it keeps every pre-existing call site
    /// compiling, and none of them can observe a difference: they configure one source, and a
    /// single source's results cannot merge with themselves unless it returns the same release
    /// twice.</para>
    /// </summary>
    public PaginationSnapshotService(
        UpstreamMergeStage mergeStage,
        SearchResultCacheStage cacheStage,
        IQuerySnapshotStore snapshotStore,
        TimeProvider timeProvider,
        TimeSpan? ttl = null,
        ISourceSetFingerprintSource? sourceSetFingerprintSource = null,
        DedupStage? dedupStage = null)
        : this(
            mergeStage,
            cacheStage,
            snapshotStore,
            timeProvider,
            new StaticSnapshotTtlSource(ttl ?? DefaultTtl),
            sourceSetFingerprintSource,
            dedupStage)
    {
    }

    /// <summary>
    /// Live-TTL constructor (M7-8c/AC24): reads the snapshot TTL from <paramref name="ttlSource"/>
    /// on every <see cref="GetPageAsync"/> call instead of capturing a fixed value at construction, so
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
        ISnapshotTtlSource ttlSource,
        ISourceSetFingerprintSource? sourceSetFingerprintSource = null,
        DedupStage? dedupStage = null)
    {
        _mergeStage = mergeStage ?? throw new ArgumentNullException(nameof(mergeStage));
        _cacheStage = cacheStage ?? throw new ArgumentNullException(nameof(cacheStage));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ttlSource = ttlSource ?? throw new ArgumentNullException(nameof(ttlSource));
        _sourceSetFingerprintSource = sourceSetFingerprintSource ?? StaticSourceSetFingerprintSource.Empty;
        _dedupStage = dedupStage ?? new DedupStage(AllEqualSourcePriority.Instance);
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

        var sourceSetFingerprint = await _sourceSetFingerprintSource.GetAsync(cancellationToken).ConfigureAwait(false);
        var snapshotToken = ComputeSnapshotToken(searchType, query, sourceSetFingerprint);
        var now = _timeProvider.GetUtcNow();

        var cached = await _snapshotStore.GetAsync(snapshotToken, now, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var payload = JsonSerializer.Deserialize<SnapshotPayload>(cached) ?? SnapshotPayload.Empty;
            // ServedFromSnapshot: this branch performs no upstream calls. Age/Band below are
            // REPLAYED from the request that materialized the snapshot and describe that request's
            // provenance, not this one's — see PagedMergeResult.ServedFromSnapshot.
            return new PagedMergeResult(
                Slice(payload.Releases, query.Offset, query.Limit),
                Array.Empty<string>(),
                payload.Age,
                payload.Band,
                ServedFromSnapshot: true);
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

                // arb-x7w8.8: dedup sits BETWEEN the merge and the cache, so what the two-age cache
                // stores is already grouped. Deduplicating on the way IN rather than on the way out
                // means the work happens once per fetch instead of once per served request, and —
                // more importantly — it means a cache hit and a fresh fetch return the SAME shape.
                // Dedup after the cache would leave already-cached entries ungrouped for the
                // remainder of their serve age, so the same query would group or not depending on
                // when it was last fetched.
                //
                // Neither the two-age cache key nor the snapshot token is affected, and both are
                // deliberately left unchanged: they are computed from the QUERY (and, for the
                // snapshot, the source-set fingerprint), never from the result set, so grouping the
                // results cannot move either key. Grouping does change the number of entries a
                // snapshot holds, which is what offset/limit slices — correctly, since a group is
                // one result.
                var deduplicated = _dedupStage.Deduplicate(merged.Releases);

                return new UpstreamFetchResult(deduplicated, Degraded: merged.RateLimitedSources.Count > 0);
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
    ///
    /// <para>
    /// The protocol IS identity-defining (#99): the two families are served from different
    /// upstream endpoints, and NZBHydra2's torznab endpoint excludes usenet indexers entirely, so
    /// the same q/cat under the two protocols legitimately resolves to two different release sets.
    /// Without the protocol in this token a Torznab search would populate the snapshot a Newznab
    /// caller then reads, handing a usenet client an all-torrent result set — the very failure
    /// #99 is about, reintroduced one layer above the URL that was fixed.
    /// </para>
    ///
    /// <para>
    /// Components are separated by a unit separator (U+001F), which cannot occur in a search type,
    /// a protocol name, or a category list, so two different component tuples cannot concatenate
    /// into one token. The previous separator-free form could: ("tvsearch", "x") and ("tvsearc",
    /// "hx") both flattened to the same string.
    /// </para>
    ///
    /// <para>
    /// <see cref="SearchQuery.Type"/> is a component alongside the raw <paramref name="searchType"/>
    /// (#104), because since that issue the two are no longer the same thing: the raw value is the
    /// inbound spelling, while the parsed <see cref="SearchType"/> is what actually selects the
    /// upstream <c>t=</c> and whether <c>season</c>/<c>ep</c> are forwarded. A query whose parsed
    /// mode differs therefore resolves to a genuinely different upstream request and must not share
    /// a snapshot, even where the raw strings coincide.
    /// </para>
    ///
    /// <para>
    /// <b>arb-u1c: <see cref="SearchQuery.Absolute"/> AND <see cref="SearchQuery.ResolvedTitle"/>
    /// are components, and the title is the one that needs justifying.</b> It is derived from
    /// <see cref="SearchQuery.TvdbId"/>, which is already hashed here, so including it does fragment
    /// snapshots that the id alone would have collapsed. It is included anyway because this token is
    /// consumed ABOVE the two-age cache — a hit returns before <see cref="SearchResultCacheStage"/>
    /// is ever reached — so a separation existing only in the two-age key is unreachable for any
    /// query a live snapshot covers. Without the title here, the first anime search issued before
    /// Sonarr is configured materialises an id-only result set that is then served back to every
    /// correctly resolved search for that episode for this snapshot's whole TTL: configuring Sonarr
    /// would appear to do nothing.
    /// </para>
    ///
    /// <para>
    /// <b>The fragmentation that buys is bounded, and deliberately so.</b> The title is a pure
    /// function of the tvdbid, so per series it takes one value per memo epoch — at most one extra
    /// split, unresolved versus resolved, not a new snapshot per request. The bound is
    /// <c>SeriesTitleResolver</c>'s memo, and specifically its SHORTER negative TTL: an unresolved
    /// answer is what selects the extra variant, so pinning it as long as a resolved one would stack
    /// the memo window on top of this TTL. If that negative TTL is ever lengthened toward the
    /// positive one, this trade-off is what it is spending.
    /// </para>
    ///
    /// <para>
    /// <b>arb-b5z: the source-set fingerprint is a component because a snapshot is a materialized
    /// RESULT SET, and which upstreams produced it is part of what it is.</b> The same q/cat against
    /// a different set of sources is a different answer and must not reuse the row. It is appended
    /// last and separated like every other component, so an EMPTY fingerprint — the default for any
    /// caller that has not been told about a source set — contributes nothing but its separator and
    /// leaves those callers producing exactly the token they produced before this was added. See
    /// <see cref="ISourceSetFingerprintSource"/> for why the failure this prevents is a cross-restart
    /// one rather than an in-process one.
    /// </para>
    /// </summary>
    private static string ComputeSnapshotToken(string searchType, SearchQuery query, string sourceSetFingerprint)
    {
        var normalizedCategories = string.Join(",", query.Categories.OrderBy(c => c));
        var raw = $"{searchType}\u001f{query.Type}\u001f{query.Protocol}\u001f{query.QueryText}\u001f{normalizedCategories}" +
                  $"\u001f{query.TvdbId}\u001f{query.TmdbId}\u001f{query.Season}\u001f{query.Episode}" +
                  $"\u001f{query.Absolute}\u001f{query.ResolvedTitle}" +
                  $"\u001f{sourceSetFingerprint}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// A single requested page (offset/limit slice) of a snapshot, plus any sources rate-limited
/// while materializing it, plus the set-level two-age cache provenance (AC-M7a-cache) every
/// served response must carry.
/// </summary>
/// <param name="ServedFromSnapshot">
/// True when this page came from an already-materialized pagination snapshot, which performs no
/// upstream calls at all.
///
/// This exists because <paramref name="CacheAge"/> and <paramref name="CacheBand"/> cannot answer
/// "was anything fetched upstream to serve this request?", and #55's AC3 needs that answered. On a
/// snapshot hit both fields are REPLAYED verbatim from the request that materialized the snapshot,
/// so a snapshot hit built from a live fetch reports Age=0/Band=Fresh — indistinguishable, on those
/// two fields alone, from the live fetch itself. Both are correct about the SET's provenance, which
/// is the question they were added for (AC-M7a-cache); neither is about this request's work.
///
/// Defaulted to false so the two constructions in <see cref="PaginationSnapshotService"/> are the
/// only places that decide it, and any future one has to opt in deliberately.
/// </param>
public sealed record PagedMergeResult(
    IReadOnlyList<RenderedRelease> Releases,
    IReadOnlyList<string> RateLimitedSources,
    TimeSpan? CacheAge,
    CacheBand CacheBand,
    bool ServedFromSnapshot = false);

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
/// be read fresh on every <see cref="PaginationSnapshotService.GetPageAsync"/> call — a setting changed
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

/// <summary>
/// arb-b5z: abstracts how <see cref="PaginationSnapshotService"/> obtains a fingerprint of the
/// source set its snapshots were materialized from, so that a snapshot built against one set of
/// upstream sources is never served to a request running against a different one.
/// </summary>
/// <remarks>
/// <para><b>THE BUG THIS EXISTS FOR IS ACROSS A RESTART, NOT WITHIN A PROCESS, and getting that
/// backwards makes the fix look like a no-op.</b> The resolved source configuration is settled once
/// at startup (<c>SourceSeeder</c> writes <c>ResolvedSourceConfiguration</c> before the first
/// request), so within one process the source set cannot change and this fingerprint cannot move.
/// What DOES change across a source edit is the process — one is required today — and
/// <see cref="IQuerySnapshotStore"/> is SQLite-backed, so snapshot rows outlive it. Without the
/// fingerprint, an operator who adds or enables a source and restarts is still served the
/// pre-restart snapshot for any identical query, for the remainder of that row's TTL: the new
/// source's releases are simply missing and nothing indicates why.</para>
///
/// <para>Putting the fingerprint in the token retires those rows by construction rather than by
/// deleting them — the new process addresses a different token, and the old rows expire unread.
/// That is why no invalidate-on-mutation hook is needed anywhere.</para>
/// </remarks>
public interface ISourceSetFingerprintSource
{
    ValueTask<string> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Wraps a fixed source-set fingerprint for the lifetime of the owning
/// <see cref="PaginationSnapshotService"/>.
/// </summary>
/// <remarks>
/// <see cref="Empty"/> is the default every pre-existing call site gets, and it is deliberately the
/// empty string: it appends nothing distinguishing to the hashed component list, so a caller that
/// has never been told about a source set produces the pre-arb-b5z token byte for byte. That keeps
/// this change invisible to every existing test and caller, and makes the fingerprint's absence a
/// value rather than a special case in <see cref="PaginationSnapshotService"/>.
/// </remarks>
public sealed class StaticSourceSetFingerprintSource(string fingerprint) : ISourceSetFingerprintSource
{
    /// <summary>The no-fingerprint default, preserving the token shape that shipped before arb-b5z.</summary>
    public static readonly StaticSourceSetFingerprintSource Empty = new(string.Empty);

    private readonly string _fingerprint = fingerprint ?? string.Empty;

    public ValueTask<string> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_fingerprint);
}
