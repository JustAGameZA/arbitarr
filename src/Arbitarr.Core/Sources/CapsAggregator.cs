namespace Arbitarr.Core.Sources;

/// <summary>
/// Merges per-source <see cref="SourceCaps"/> (fetched from one or more <see cref="IUpstreamSource"/>
/// instances) into a single <see cref="SourceCaps"/> to advertise to *arr consumers.
///
/// Aggregation rules (AC5):
/// - Categories: UNION across all sources, retaining upstream names. Where sources disagree on a
///   name for an ID, the first configured source wins.
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
        SearchProtocol protocol,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var perSourceCaps = new List<SourceCaps>();

        foreach (var source in sources)
        {
            var caps = await FetchWithFallbackAsync(source, protocol, cancellationToken).ConfigureAwait(false);
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

        // Categories: preserve everything the configured sources expose, including books.
        var unionCategories = perSourceCaps
            .SelectMany(c => c.SupportedCategories)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var categoryNames = new Dictionary<int, string>();
        foreach (var caps in perSourceCaps)
        {
            if (caps.CategoryNames is null)
            {
                continue;
            }

            foreach (var (categoryId, categoryName) in caps.CategoryNames)
            {
                if (unionCategories.Contains(categoryId) && string.IsNullOrWhiteSpace(categoryName) is false)
                {
                    categoryNames.TryAdd(categoryId, categoryName);
                }
            }
        }

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
            SupportsAnimeSearch: perSourceCaps.Any(c => c.SupportsAnimeSearch),
            CategoryNames: categoryNames);
    }

    /// <summary>
    /// The last-known-good cache key for one source under one protocol family (#99).
    ///
    /// <para>
    /// Scoped by protocol, not just by source name. Caps are now fetched per protocol, and
    /// NZBHydra2 answers its two endpoints differently (the torznab endpoint advertises only its
    /// torrent indexers' categories). Keying on the source name alone would let a Torznab caps
    /// fetch overwrite the Newznab last-known-good row and vice versa, so an upstream that went
    /// down would be backfilled from the OTHER protocol's categories — a wrong answer that looks
    /// like a working fallback. <see cref="ICapsCacheStore"/> treats the key as opaque, so this
    /// needs no store or schema change.
    /// </para>
    ///
    /// <para>
    /// Public because it is the ONLY definition of the convention: anything that seeds or inspects
    /// the store directly (a test pre-seeding a last-known-good row, say) has to agree with what
    /// this type writes, and a second hand-built copy of the format would drift silently — the
    /// seeded row would simply never be found, and the fallback it was meant to exercise would go
    /// untested while still looking green.
    /// </para>
    /// </summary>
    public static string CacheKey(string sourceName, SearchProtocol protocol) =>
        $"{sourceName}#{protocol}";

    private async Task<SourceCaps?> FetchWithFallbackAsync(
        IUpstreamSource source,
        SearchProtocol protocol,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKey(source.Name, protocol);

        try
        {
            var caps = await source.GetCapsAsync(protocol, cancellationToken).ConfigureAwait(false);
            await _cacheStore.SaveAsync(cacheKey, caps, cancellationToken).ConfigureAwait(false);
            return caps;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            // Fetch failed (exception, timeout, non-success surfaced as an exception by the
            // adapter) — fall back to the last-known-good cached caps for this source rather
            // than dropping it from the merge or contributing empty/default caps.
            return await _cacheStore.GetLastKnownGoodAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
    }
}
