using System.Collections.Concurrent;
using Arbitarr.Core.Caching;

namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// M7 step 7 observability: process-lifetime, in-memory counters populated by the pipeline stages
/// (results in, suppressions by source and reason, LLM calls/failures, verdict-cache and
/// search-result-cache hit rates, served-age distribution). A singleton like
/// <see cref="RecentSearchLog"/>: writers are lock-free (<see cref="Interlocked"/> +
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>) so recording never slows the request path,
/// and the counters are best-effort diagnostics — P1 fail-open: every stage treats a missing
/// counters instance as a no-op, and no counter ever influences a verdict or a response.
/// </summary>
public sealed class ObservabilityCounters
{
    /// <summary>Served-age buckets (upper bounds, exclusive) for the search-result-cache age distribution.</summary>
    public static readonly IReadOnlyList<(string Label, TimeSpan UpperBound)> ServedAgeBuckets =
    [
        ("lt1m", TimeSpan.FromMinutes(1)),
        ("1m-5m", TimeSpan.FromMinutes(5)),
        ("5m-15m", TimeSpan.FromMinutes(15)),
        ("15m-60m", TimeSpan.FromMinutes(60)),
        ("gte60m", TimeSpan.MaxValue),
    ];

    private long _resultsIn;
    private long _suppressedTotal;
    private long _llmCalls;
    private long _llmFailures;
    private long _verdictCacheHits;
    private long _verdictCacheMisses;
    private long _searchCacheFresh;
    private long _searchCacheStaleButValid;
    private long _searchCacheFetched;
    private long _searchCacheDegradedMisses;

    private readonly ConcurrentDictionary<string, StrongBox> _suppressedBySourceAndReason = new(StringComparer.Ordinal);
    private readonly long[] _servedAgeCounts = new long[ServedAgeBuckets.Count];

    /// <summary>Records releases entering the filter stage for one request.</summary>
    public void RecordResultsIn(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _resultsIn, count);
        }
    }

    /// <summary>Records one release suppressed by <paramref name="source"/> for <paramref name="reason"/> (rule name or source label).</summary>
    public void RecordSuppressed(string source, string reason)
    {
        Interlocked.Increment(ref _suppressedTotal);
        var box = _suppressedBySourceAndReason.GetOrAdd($"{source}:{reason}", static _ => new StrongBox());
        Interlocked.Increment(ref box.Value);
    }

    /// <summary>Records one verdict-cache lookup by the request path.</summary>
    public void RecordVerdictCacheLookup(bool hit) =>
        Interlocked.Increment(ref hit ? ref _verdictCacheHits : ref _verdictCacheMisses);

    /// <summary>Records one classifier call to the LLM; <paramref name="failed"/> when it failed open (no verdict written).</summary>
    public void RecordLlmCall(bool failed)
    {
        Interlocked.Increment(ref _llmCalls);
        if (failed)
        {
            Interlocked.Increment(ref _llmFailures);
        }
    }

    /// <summary>Records a search-result-cache hit served from the store in <paramref name="band"/> at <paramref name="age"/>.</summary>
    public void RecordSearchCacheHit(CacheBand band, TimeSpan? age)
    {
        Interlocked.Increment(ref band == CacheBand.StaleButValid ? ref _searchCacheStaleButValid : ref _searchCacheFresh);
        RecordServedAge(age);
    }

    /// <summary>Records a search-result-cache miss that was answered by an inline upstream fetch.</summary>
    public void RecordSearchCacheFetched()
    {
        Interlocked.Increment(ref _searchCacheFetched);
        RecordServedAge(TimeSpan.Zero);
    }

    /// <summary>Records a search-result-cache miss where upstream was degraded and nothing servable existed.</summary>
    public void RecordSearchCacheDegradedMiss() => Interlocked.Increment(ref _searchCacheDegradedMisses);

    private void RecordServedAge(TimeSpan? age)
    {
        if (age is null)
        {
            return;
        }

        for (var i = 0; i < ServedAgeBuckets.Count; i++)
        {
            if (age.Value < ServedAgeBuckets[i].UpperBound)
            {
                Interlocked.Increment(ref _servedAgeCounts[i]);
                return;
            }
        }
    }

    /// <summary>Point-in-time copy of every counter. Counts read individually; the snapshot is not atomic across counters.</summary>
    public ObservabilitySnapshot Snapshot()
    {
        var suppressed = _suppressedBySourceAndReason
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => Interlocked.Read(ref kv.Value.Value), StringComparer.Ordinal);

        var servedAge = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < ServedAgeBuckets.Count; i++)
        {
            servedAge[ServedAgeBuckets[i].Label] = Interlocked.Read(ref _servedAgeCounts[i]);
        }

        var verdictHits = Interlocked.Read(ref _verdictCacheHits);
        var verdictMisses = Interlocked.Read(ref _verdictCacheMisses);
        var fresh = Interlocked.Read(ref _searchCacheFresh);
        var stale = Interlocked.Read(ref _searchCacheStaleButValid);
        var fetched = Interlocked.Read(ref _searchCacheFetched);
        var degraded = Interlocked.Read(ref _searchCacheDegradedMisses);

        return new ObservabilitySnapshot(
            ResultsIn: Interlocked.Read(ref _resultsIn),
            SuppressedTotal: Interlocked.Read(ref _suppressedTotal),
            SuppressedBySourceAndReason: suppressed,
            LlmCalls: Interlocked.Read(ref _llmCalls),
            LlmFailures: Interlocked.Read(ref _llmFailures),
            VerdictCache: new HitRate(verdictHits, verdictMisses),
            SearchCache: new SearchCacheStats(fresh, stale, fetched, degraded, Rate(fresh + stale, fresh + stale + fetched + degraded)),
            ServedAgeDistribution: servedAge);
    }

    internal static double? Rate(long hits, long total) => total == 0 ? null : (double)hits / total;

    private sealed class StrongBox
    {
        public long Value;
    }
}

/// <summary>Hits vs misses plus the derived rate (null until at least one lookup happened).</summary>
public sealed record HitRate(long Hits, long Misses)
{
    public double? Rate => ObservabilityCounters.Rate(Hits, Hits + Misses);
}

/// <summary>Search-result-cache reads by band: served hits (fresh / stale-but-valid) vs misses (inline fetch / degraded).</summary>
public sealed record SearchCacheStats(long FreshHits, long StaleButValidHits, long FetchedMisses, long DegradedMisses, double? HitRate);

/// <summary>Point-in-time copy of <see cref="ObservabilityCounters"/>.</summary>
public sealed record ObservabilitySnapshot(
    long ResultsIn,
    long SuppressedTotal,
    IReadOnlyDictionary<string, long> SuppressedBySourceAndReason,
    long LlmCalls,
    long LlmFailures,
    HitRate VerdictCache,
    SearchCacheStats SearchCache,
    IReadOnlyDictionary<string, long> ServedAgeDistribution);
