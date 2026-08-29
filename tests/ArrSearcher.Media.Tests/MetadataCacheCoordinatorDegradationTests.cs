using ArrSearcher.Core.Identity;
using ArrSearcher.Media.Cache;
using Xunit;

namespace ArrSearcher.Media.Tests;

/// <summary>
/// AC-M6/AC-M6a: cache-absent, source-unreachable, and no-xem-coverage must each be distinctly
/// represented via <see cref="MatchProvenanceFlags"/> - never collapsed into one generic failure -
/// and a confirmed no-coverage outcome must be negative-cached. AC-M8: cache invalidation is driven
/// solely by <see cref="SourceSnapshotHasher"/> content hashing, never by trusting freshness headers
/// (there are none to trust here - the fetch delegate itself decides Success/Unreachable/NoCoverage).
/// </summary>
public class MetadataCacheCoordinatorDegradationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolveAsync_CacheAbsent_AndFetchUnreachable_ReturnsCacheAbsentAndSourceUnreachable_BothFlagsSet()
    {
        var store = new FakeMetadataCacheStore();
        var coordinator = new MetadataCacheCoordinator(store);

        var result = await coordinator.ResolveAsync(
            "bleach-tvdb-74796",
            "xem",
            _ => Task.FromResult(MetadataFetchOutcome.Unreachable()),
            now: Now);

        Assert.True(result.Flags.HasFlag(MatchProvenanceFlags.CacheAbsent));
        Assert.True(result.Flags.HasFlag(MatchProvenanceFlags.SourceUnreachable));
        Assert.Null(result.PayloadJson);
    }

    [Fact]
    public async Task ResolveAsync_CacheAbsent_AndFetchUnreachable_DoesNotSetNoXemCoverageFlag()
    {
        // Regression against collapsing distinct degraded states into one generic failure bit:
        // an unreachable source with no prior cache is NOT the same thing as a confirmed absence
        // of XEM coverage, and must not carry that flag.
        var store = new FakeMetadataCacheStore();
        var coordinator = new MetadataCacheCoordinator(store);

        var result = await coordinator.ResolveAsync(
            "bleach-tvdb-74796",
            "xem",
            _ => Task.FromResult(MetadataFetchOutcome.Unreachable()),
            now: Now);

        Assert.False(result.Flags.HasFlag(MatchProvenanceFlags.NoXemCoverage));
    }

    [Fact]
    public async Task ResolveAsync_StaleLastKnownGoodCache_AndFetchUnreachable_FallsBackToLastKnownGood_WithSourceUnreachableFlag()
    {
        var store = new FakeMetadataCacheStore();
        store.SeedHit("bleach-tvdb-74796", "xem", "{\"stale\":true}", "abc123", refreshAfter: Now.AddHours(-1));
        var coordinator = new MetadataCacheCoordinator(store);

        var result = await coordinator.ResolveAsync(
            "bleach-tvdb-74796",
            "xem",
            _ => Task.FromResult(MetadataFetchOutcome.Unreachable()),
            now: Now);

        Assert.Equal("{\"stale\":true}", result.PayloadJson);
        Assert.True(result.Flags.HasFlag(MatchProvenanceFlags.SourceUnreachable));
        Assert.False(result.Flags.HasFlag(MatchProvenanceFlags.CacheAbsent));
    }

    [Fact]
    public async Task ResolveAsync_StaleNegativeCache_AndFetchUnreachable_FallsBackToNegative_WithBothUnreachableAndNoCoverageFlags()
    {
        var store = new FakeMetadataCacheStore();
        store.SeedNegativeHit("obscure-series", "xem", "negativehash", refreshAfter: Now.AddHours(-1));
        var coordinator = new MetadataCacheCoordinator(store);

        var result = await coordinator.ResolveAsync(
            "obscure-series",
            "xem",
            _ => Task.FromResult(MetadataFetchOutcome.Unreachable()),
            now: Now);

        Assert.Null(result.PayloadJson);
        Assert.True(result.Flags.HasFlag(MatchProvenanceFlags.SourceUnreachable));
        Assert.True(result.Flags.HasFlag(MatchProvenanceFlags.NoXemCoverage));
    }

    [Fact]
    public async Task ResolveAsync_FreshNegativeCacheHit_ServesWithoutLiveFetch_NoXemCoverageFlagOnly()
    {
        var store = new FakeMetadataCacheStore();
        store.SeedNegativeHit("obscure-series", "xem", "negativehash", refreshAfter: Now.AddHours(1));
        var coordinator = new MetadataCacheCoordinator(store);
        var fetchWasCalled = false;

        var result = await coordinator.ResolveAsync(
            "obscure-series",
            "xem",
            _ => { fetchWasCalled = true; return Task.FromResult(MetadataFetchOutcome.Success("{}")); },
            now: Now);

        Assert.False(fetchWasCalled);
        Assert.Equal(MatchProvenanceFlags.NoXemCoverage, result.Flags);
    }

    [Fact]
    public async Task ResolveAsync_FetchAffirmativelyReportsNoCoverage_NegativeCachesTheResult()
    {
        var store = new FakeMetadataCacheStore();
        var coordinator = new MetadataCacheCoordinator(store);

        var result = await coordinator.ResolveAsync(
            "obscure-series",
            "xem",
            _ => Task.FromResult(MetadataFetchOutcome.NoCoverage()),
            now: Now);

        Assert.Equal(1, store.SaveNegativeCallCount);
        Assert.Equal(MatchProvenanceFlags.NoXemCoverage, result.Flags);
        Assert.Null(result.PayloadJson);
    }

    [Fact]
    public async Task ResolveAsync_SubsequentLookupAfterNegativeCache_DoesNotRefetchWithinRefreshWindow()
    {
        // Direct assertion of negative-caching's purpose: a second lookup for the same known-absent
        // series, within the refresh window, must not call the fetch delegate again.
        var store = new FakeMetadataCacheStore();
        var coordinator = new MetadataCacheCoordinator(store, refreshInterval: TimeSpan.FromHours(24));
        var fetchCallCount = 0;

        await coordinator.ResolveAsync("obscure-series", "xem", _ => { fetchCallCount++; return Task.FromResult(MetadataFetchOutcome.NoCoverage()); }, now: Now);
        await coordinator.ResolveAsync("obscure-series", "xem", _ => { fetchCallCount++; return Task.FromResult(MetadataFetchOutcome.NoCoverage()); }, now: Now.AddHours(1));

        Assert.Equal(1, fetchCallCount);
    }

    [Fact]
    public async Task ResolveAsync_SuccessfulFetch_UnchangedContentHash_StillPersistsButPayloadStable()
    {
        // AC-M8: invalidation is hash-driven, not header-driven. Fetching the same raw content twice
        // yields the same snapshot hash both times - there is no freshness header in this contract at
        // all, so "no change" is entirely a function of ComputeHash's determinism.
        var store = new FakeMetadataCacheStore();
        var coordinator = new MetadataCacheCoordinator(store);
        const string rawContent = "{\"season\":17,\"episode\":36}";

        var first = await coordinator.ResolveAsync("bleach-tvdb-74796", "xem", _ => Task.FromResult(MetadataFetchOutcome.Success(rawContent)), now: Now);
        var second = await coordinator.ResolveAsync("bleach-tvdb-74796", "xem", _ => Task.FromResult(MetadataFetchOutcome.Success(rawContent)), now: Now.AddHours(25));

        Assert.Equal(first.SourceSnapshotVersion, second.SourceSnapshotVersion);
        Assert.Equal(SourceSnapshotHasher.ComputeHash(rawContent), first.SourceSnapshotVersion);
    }

    [Fact]
    public async Task ResolveAsync_SuccessfulFetch_ChangedUpstreamContent_ProducesDifferentSnapshotHash()
    {
        var store = new FakeMetadataCacheStore();
        var coordinator = new MetadataCacheCoordinator(store);

        var first = await coordinator.ResolveAsync("bleach-tvdb-74796", "xem", _ => Task.FromResult(MetadataFetchOutcome.Success("{\"v\":1}")), now: Now);
        var second = await coordinator.ResolveAsync("bleach-tvdb-74796", "xem", _ => Task.FromResult(MetadataFetchOutcome.Success("{\"v\":2}")), now: Now.AddHours(25));

        Assert.NotEqual(first.SourceSnapshotVersion, second.SourceSnapshotVersion);
    }

    [Fact]
    public async Task ResolveAsync_FreshPositiveCacheHit_ServesWithoutLiveFetch_NoDegradationFlags()
    {
        var store = new FakeMetadataCacheStore();
        store.SeedHit("bleach-tvdb-74796", "xem", "{\"cached\":true}", "hash1", refreshAfter: Now.AddHours(1));
        var coordinator = new MetadataCacheCoordinator(store);
        var fetchWasCalled = false;

        var result = await coordinator.ResolveAsync(
            "bleach-tvdb-74796",
            "xem",
            _ => { fetchWasCalled = true; return Task.FromResult(MetadataFetchOutcome.Success("{}")); },
            now: Now);

        Assert.False(fetchWasCalled);
        Assert.Equal(MatchProvenanceFlags.None, result.Flags);
        Assert.Equal("{\"cached\":true}", result.PayloadJson);
    }

    [Fact]
    public void SourceSnapshotHasher_ComputeHash_IsDeterministic_SameContentProducesSameHash()
    {
        var first = SourceSnapshotHasher.ComputeHash("identical content");
        var second = SourceSnapshotHasher.ComputeHash("identical content");

        Assert.Equal(first, second);
    }

    [Fact]
    public void SourceSnapshotHasher_ComputeHash_DifferentContentProducesDifferentHash()
    {
        var first = SourceSnapshotHasher.ComputeHash("content A");
        var second = SourceSnapshotHasher.ComputeHash("content B");

        Assert.NotEqual(first, second);
    }
}
