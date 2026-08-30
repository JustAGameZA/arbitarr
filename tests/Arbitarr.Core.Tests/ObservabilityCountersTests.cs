using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>M7 step 7: <see cref="ObservabilityCounters"/> arithmetic and bucketing.</summary>
public class ObservabilityCountersTests
{
    [Fact]
    public void FreshSnapshot_HasZeroCounts_AndNullRates()
    {
        var snapshot = new ObservabilityCounters().Snapshot();

        Assert.Equal(0, snapshot.ResultsIn);
        Assert.Equal(0, snapshot.SuppressedTotal);
        Assert.Empty(snapshot.SuppressedBySourceAndReason);
        Assert.Null(snapshot.VerdictCache.Rate);
        Assert.Null(snapshot.SearchCache.HitRate);
        Assert.Equal(ObservabilityCounters.ServedAgeBuckets.Select(b => b.Label), snapshot.ServedAgeDistribution.Keys);
        Assert.All(snapshot.ServedAgeDistribution.Values, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Suppressions_AreKeyedBySourceAndReason_AndTotalled()
    {
        var counters = new ObservabilityCounters();
        counters.RecordSuppressed("DenyRule", "no-cam");
        counters.RecordSuppressed("DenyRule", "no-cam");
        counters.RecordSuppressed("AI", "not-the-series");

        var snapshot = counters.Snapshot();

        Assert.Equal(3, snapshot.SuppressedTotal);
        Assert.Equal(2, snapshot.SuppressedBySourceAndReason["DenyRule:no-cam"]);
        Assert.Equal(1, snapshot.SuppressedBySourceAndReason["AI:not-the-series"]);
    }

    [Fact]
    public void HitRates_AreHitsOverLookups()
    {
        var counters = new ObservabilityCounters();
        counters.RecordVerdictCacheLookup(hit: true);
        counters.RecordVerdictCacheLookup(hit: true);
        counters.RecordVerdictCacheLookup(hit: false);
        counters.RecordVerdictCacheLookup(hit: false);
        counters.RecordSearchCacheHit(CacheBand.Fresh, TimeSpan.FromSeconds(10));
        counters.RecordSearchCacheHit(CacheBand.StaleButValid, TimeSpan.FromMinutes(20));
        counters.RecordSearchCacheFetched();
        counters.RecordSearchCacheDegradedMiss();

        var snapshot = counters.Snapshot();

        Assert.Equal(0.5, snapshot.VerdictCache.Rate);
        Assert.Equal(1, snapshot.SearchCache.FreshHits);
        Assert.Equal(1, snapshot.SearchCache.StaleButValidHits);
        Assert.Equal(1, snapshot.SearchCache.FetchedMisses);
        Assert.Equal(1, snapshot.SearchCache.DegradedMisses);
        Assert.Equal(0.5, snapshot.SearchCache.HitRate);
    }

    [Fact]
    public void ServedAges_FallIntoBuckets_AndFetchesCountAsZeroAge()
    {
        var counters = new ObservabilityCounters();
        counters.RecordSearchCacheFetched();
        counters.RecordSearchCacheHit(CacheBand.Fresh, TimeSpan.FromMinutes(1));
        counters.RecordSearchCacheHit(CacheBand.Fresh, TimeSpan.FromMinutes(7));
        counters.RecordSearchCacheHit(CacheBand.StaleButValid, TimeSpan.FromMinutes(30));
        counters.RecordSearchCacheHit(CacheBand.StaleButValid, TimeSpan.FromHours(3));
        counters.RecordSearchCacheHit(CacheBand.StaleButValid, age: null);

        var ages = counters.Snapshot().ServedAgeDistribution;

        Assert.Equal(1, ages["lt1m"]);
        Assert.Equal(1, ages["1m-5m"]);
        Assert.Equal(1, ages["5m-15m"]);
        Assert.Equal(1, ages["15m-60m"]);
        Assert.Equal(1, ages["gte60m"]);
        Assert.Equal(5, ages.Values.Sum());
    }

    [Fact]
    public void LlmCalls_CountFailuresSeparately_AndResultsInIgnoresNonPositive()
    {
        var counters = new ObservabilityCounters();
        counters.RecordLlmCall(failed: false);
        counters.RecordLlmCall(failed: true);
        counters.RecordResultsIn(0);
        counters.RecordResultsIn(-1);
        counters.RecordResultsIn(4);

        var snapshot = counters.Snapshot();

        Assert.Equal(2, snapshot.LlmCalls);
        Assert.Equal(1, snapshot.LlmFailures);
        Assert.Equal(4, snapshot.ResultsIn);
    }
}
