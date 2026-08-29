namespace Arbitarr.Core.Sources;

/// <summary>
/// Merges per-source <see cref="SourceCaps"/> (fetched from one or more <see cref="IUpstreamSource"/>
/// instances) into a single <see cref="SourceCaps"/> to advertise to *arr consumers.
///
/// Aggregation rules (AC5, AC5a-i):
/// - Categories: UNION across all sources, including anime as selectable if ANY source supports it.
/// - Book: NEVER advertised, structurally, regardless of what any upstream reports. This is
///   enforced unconditionally on every merge, not merely "happens to not appear".
/// - SupportedParams: INTERSECTION across sources. A param missing from the merged set means at
///   least one source doesn't support it; callers should degrade to keyword search plus local
///   post-filtering for that source when using it — that degradation logic is out of scope here.
/// - MaxPageSize (limits max): always 100, enforced by us, regardless of what any single
///   upstream advertises.
///
/// Last-known-good caching: if fetching caps from a source fails (exception, timeout, or
/// non-success), the aggregator falls back to <see cref="ICapsCacheStore"/>'s most recently
/// cached caps for that source so a dead upstream cannot narrow the merged/advertised caps.
/// A source that fails AND has no prior cached caps contributes nothing to the merge (it cannot
/// meaningfully participate), but is never allowed to drag the merge down to empty/default caps
/// on its own.
/// </summary>
public sealed class CapsAggregator
{
    /// <summary>Our own enforced limits-max value, independent of any upstream's advertised value.</summary>
    public const int EnforcedMaxPageSize = 100;

    private readonly ICapsCacheStore _cacheStore;

    public CapsAggregator(ICapsCacheStore cacheStore)
    {
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
    }

    /// <summary>
    /// Fetches caps from each source (falling back to last-known-good on failure, and caching
    /// successful fetches), then merges the results per the rules documented on this type.
    /// </summary>
    public async Task<SourceCaps> AggregateAsync(
        IReadOnlyList<IUpstreamSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var perSourceCaps = new List<SourceCaps>();

        foreach (var source in sources)
        {
            var caps = await FetchWithFallbackAsync(source, cancellationToken).ConfigureAwait(false);
            if (caps is not null)
            {
                perSourceCaps.Add(caps);
            }
        }

        return Merge(perSourceCaps);
    }

    /// <summary>
    /// Merges a set of already-fetched per-source caps (e.g. when fetch/fallback has already
    /// happened, or for direct unit testing of merge semantics).
    /// </summary>
    public static SourceCaps Merge(IReadOnlyList<SourceCaps> perSourceCaps)
    {
        ArgumentNullException.ThrowIfNull(perSourceCaps);

        if (perSourceCaps.Count == 0)
        {
            return new SourceCaps(
                SupportedCategories: Array.Empty<int>(),
                SupportsTvSearch: false,
                SupportsMovieSearch: false,
                MaxPageSize: EnforcedMaxPageSize,
                SupportedParams: Array.Empty<string>(),
                SupportsAnimeSearch: false);
        }

        // Categories: union, then structurally strip any book category no matter its source.
        var unionCategories = perSourceCaps
            .SelectMany(c => c.SupportedCategories)
            .Distinct()
            .Except(SourceCaps.BookCategoryIds)
            .OrderBy(id => id)
            .ToArray();

        // SupportedParams: intersection across all sources. Treat a null list as "no params
        // known/advertised" so it can never silently inflate the intersection.
        IEnumerable<string>? paramIntersection = null;
        foreach (var caps in perSourceCaps)
        {
            var sourceParams = caps.SupportedParams ?? Array.Empty<string>();
            paramIntersection = paramIntersection is null
                ? sourceParams
                : paramIntersection.Intersect(sourceParams, StringComparer.OrdinalIgnoreCase);
        }

        var mergedParams = (paramIntersection ?? Enumerable.Empty<string>())
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SourceCaps(
            SupportedCategories: unionCategories,
            SupportsTvSearch: perSourceCaps.Any(c => c.SupportsTvSearch),
            SupportsMovieSearch: perSourceCaps.Any(c => c.SupportsMovieSearch),
            MaxPageSize: EnforcedMaxPageSize,
            SupportedParams: mergedParams,
            SupportsAnimeSearch: perSourceCaps.Any(c => c.SupportsAnimeSearch));
    }

    private async Task<SourceCaps?> FetchWithFallbackAsync(IUpstreamSource source, CancellationToken cancellationToken)
    {
        try
        {
            var caps = await source.GetCapsAsync(cancellationToken).ConfigureAwait(false);
            await _cacheStore.SaveAsync(source.Name, caps, cancellationToken).ConfigureAwait(false);
            return caps;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            // Fetch failed (exception, timeout, non-success surfaced as an exception by the
            // adapter) — fall back to the last-known-good cached caps for this source rather
            // than dropping it from the merge or contributing empty/default caps.
            return await _cacheStore.GetLastKnownGoodAsync(source.Name, cancellationToken).ConfigureAwait(false);
        }
    }
}
