namespace Arbitarr.Core.Sources;

/// <summary>
/// Merges per-source <see cref="SourceCaps"/> (fetched from one or more <see cref="IUpstreamSource"/>
/// instances) into a single <see cref="SourceCaps"/> to advertise to *arr consumers.
///
/// Aggregation rules (AC5):
/// - Categories: UNION across all sources, retaining upstream names. Where sources disagree on a
///   name for an ID, the first configured source wins.
/// - Book categories (the 7000 family): ALWAYS EXCLUDED, unconditionally, whatever any upstream
///   advertises. This is not a union member that happens to be absent — it is a subtraction applied
///   after the union, so a source advertising 7020 (even the only source doing so) cannot put it
///   back. Arbitarr fronts Sonarr and Radarr, neither of which can act on a book result, so
///   offering the category only produces searches whose every result is unusable.
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

    /// <summary>
    /// Inclusive lower bound of the Newznab book category family; <see cref="BookCategoryMaxExclusive"/>
    /// is its exclusive upper bound. The whole family is dropped from the merge — see
    /// <see cref="IsBookCategory"/> for why the test is the FAMILY rather than an enumerated list.
    /// </summary>
    public const int BookCategoryMin = 7000;

    /// <summary>Exclusive upper bound of the book category family. See <see cref="BookCategoryMin"/>.</summary>
    public const int BookCategoryMaxExclusive = 8000;

    /// <summary>
    /// Whether a Newznab category id belongs to the book family, which the merge excludes
    /// unconditionally (CONTEXT.md "Caps aggregation").
    ///
    /// <para>The test is the RANGE, never a list of the ids anyone has actually seen. Upstreams mint
    /// sub-categories in this family freely (7000 Books, 7020 EBook, 7030 Comics, 7060 Mags, and
    /// whatever a given tracker adds next), so an enumerated list is wrong the first time an upstream
    /// advertises an id nobody wrote down — and it is wrong in the direction that FAILS OPEN, quietly
    /// offering *arr a category whose every result it cannot act on. A range cannot be missed that
    /// way.</para>
    ///
    /// <para>Public because the exclusion is a documented property of the merged caps, so a test
    /// asserting it per category id has to agree with what this type actually does rather than
    /// re-deriving the bound and drifting from it.</para>
    /// </summary>
    public static bool IsBookCategory(int categoryId) =>
        categoryId >= BookCategoryMin && categoryId < BookCategoryMaxExclusive;

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

        // Categories: union across sources, MINUS the book family. The subtraction is applied to the
        // union rather than to each source's list so it reads as what it is — the documented
        // exception carved out of "offered if ANY source supports it" (CONTEXT.md "Caps
        // aggregation"), not a per-source quirk. It is unconditional: neither the number of sources
        // advertising 7020 nor its being advertised by only one of them can put it back.
        var unionCategories = perSourceCaps
            .SelectMany(c => c.SupportedCategories)
            .Distinct()
            .Where(id => IsBookCategory(id) is false)
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

    /// <summary>
    /// Every protocol family a source's stored caps are keyed under — that is, exactly the set of
    /// keys one source name expands into through <see cref="CacheKey"/>.
    ///
    /// <para>Lives beside <see cref="CacheKey"/> and is public for the same reason it is: the key
    /// convention and the set of protocols it spans are one fact, and a caller that has to enumerate
    /// a source's keys (<see cref="CapsRefresher"/> writing them, <see cref="ICapsCacheStore"/>'s
    /// delete removing them) must not carry its own copy of the list. A copy that fell behind a
    /// third protocol family would leave that family's entry written but never removed — the stale
    /// row a later same-named source would silently adopt.</para>
    /// </summary>
    public static readonly IReadOnlyList<SearchProtocol> AllProtocols =
        [SearchProtocol.Torznab, SearchProtocol.Newznab];

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
