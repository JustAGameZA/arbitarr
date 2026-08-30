using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arbitarr.Api.Rendering;
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
/// On a cache miss (no live snapshot for this query's key), the full merge fans out to every
/// upstream source via <see cref="UpstreamMergeStage"/> and the result is persisted with the
/// configured TTL; a cache hit slices directly from the persisted snapshot with zero upstream
/// calls.
/// </summary>
public sealed class PaginationSnapshotService
{
    /// <summary>
    /// Default query-snapshot TTL (300s), matching <c>SettingKey.QuerySnapshotTtl</c>'s
    /// documented default. M1 does not yet wire the settings-persistence pipeline into the Host,
    /// so this constant stands in for it; once settings persistence lands, this should read the
    /// live <c>SettingsSnapshot.QuerySnapshotTtl</c> value instead.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(300);

    private readonly UpstreamMergeStage _mergeStage;
    private readonly IQuerySnapshotStore _snapshotStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    public PaginationSnapshotService(
        UpstreamMergeStage mergeStage,
        IQuerySnapshotStore snapshotStore,
        TimeProvider timeProvider,
        TimeSpan? ttl = null)
    {
        _mergeStage = mergeStage ?? throw new ArgumentNullException(nameof(mergeStage));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Resolves the full, order-stable merged release set for the query identity carried by
    /// <paramref name="query"/> (ignoring its Offset/Limit), from a live snapshot if one exists,
    /// or by materializing a fresh one via <see cref="UpstreamMergeStage"/> otherwise. Also
    /// returns the set of rate-limited source names from a fresh merge (empty on a snapshot hit,
    /// since a snapshot hit performs no upstream calls).
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
            var releases = JsonSerializer.Deserialize<RenderedRelease[]>(cached) ?? Array.Empty<RenderedRelease>();
            return new PagedMergeResult(Slice(releases, query.Offset, query.Limit), Array.Empty<string>());
        }

        var merged = await _mergeStage.MergeAsync(query, cancellationToken).ConfigureAwait(false);

        // Only persist a snapshot when the merge actually produced results or every source is
        // healthy-but-empty; a fully rate-limited merge should not be cached, so the next request
        // gets a fresh chance once sources recover.
        if (merged.Releases.Count > 0 || merged.RateLimitedSources.Count == 0)
        {
            var payloadJson = JsonSerializer.Serialize(merged.Releases);
            await _snapshotStore.SaveAsync(snapshotToken, payloadJson, now, _ttl, cancellationToken).ConfigureAwait(false);
        }

        return new PagedMergeResult(Slice(merged.Releases, query.Offset, query.Limit), merged.RateLimitedSources);
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

/// <summary>A single requested page (offset/limit slice) of a snapshot, plus any sources rate-limited while materializing it.</summary>
public sealed record PagedMergeResult(IReadOnlyList<RenderedRelease> Releases, IReadOnlyList<string> RateLimitedSources);
