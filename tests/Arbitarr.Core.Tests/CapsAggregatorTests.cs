using ArrSearcher.Core.Releases;
using ArrSearcher.Core.Sources;

namespace ArrSearcher.Core.Tests;

/// <summary>
/// Proves <see cref="CapsAggregator"/>'s merge semantics (AC5, AC5a-i): union of categories
/// (including anime), a structural exclusion of "book" regardless of upstream input,
/// intersection of supported params, our own enforced limits-max of 100, and last-known-good
/// fallback when a source's caps fetch fails.
/// </summary>
public class CapsAggregatorTests
{
    // ---------- Categories: union, including anime ----------

    [Fact]
    public void Merge_UnionsCategoriesAcrossSources()
    {
        var capsA = new SourceCaps(new[] { 5000, 5030 }, true, false, 50);
        var capsB = new SourceCaps(new[] { 5030, 2000 }, false, true, 25);

        var merged = CapsAggregator.Merge(new[] { capsA, capsB });

        Assert.Equal(new[] { 2000, 5000, 5030 }, merged.SupportedCategories.OrderBy(x => x));
    }

    [Fact]
    public void Merge_IncludesAnimeAsSelectable_IfAnySourceSupportsIt()
    {
        var capsWithAnime = new SourceCaps(new[] { 5070 }, true, false, 50, SupportsAnimeSearch: true);
        var capsWithoutAnime = new SourceCaps(new[] { 5000 }, true, false, 50, SupportsAnimeSearch: false);

        var merged = CapsAggregator.Merge(new[] { capsWithAnime, capsWithoutAnime });

        Assert.True(merged.SupportsAnimeSearch);
        Assert.Contains(5070, merged.SupportedCategories);
    }

    // ---------- Book: structural, hard exclusion (the single most important behavior here) ----------

    [Fact]
    public void Merge_NeverAdvertisesBookCategory_EvenIfSourceClaimsSupport()
    {
        // A fake source's caps claim support for the standard Torznab "Books" category (7000)
        // and one of its subcategories (7020, EBook), alongside legitimate TV categories.
        var bookClaimingCaps = new SourceCaps(
            SupportedCategories: new[] { 5000, 7000, 7020 },
            SupportsTvSearch: true,
            SupportsMovieSearch: false,
            MaxPageSize: 100);

        var otherCaps = new SourceCaps(
            SupportedCategories: new[] { 2000 },
            SupportsTvSearch: false,
            SupportsMovieSearch: true,
            MaxPageSize: 50);

        var merged = CapsAggregator.Merge(new[] { bookClaimingCaps, otherCaps });

        // This asserts the merge actually STRIPPED book categories that were genuinely present
        // in the input, not merely that book never appeared because no input ever claimed it.
        Assert.DoesNotContain(7000, merged.SupportedCategories);
        Assert.DoesNotContain(7020, merged.SupportedCategories);
        foreach (var bookId in SourceCaps.BookCategoryIds)
        {
            Assert.DoesNotContain(bookId, merged.SupportedCategories);
        }

        // Non-book categories from the same claiming source must still survive the merge —
        // proving this isn't just an empty/degenerate result.
        Assert.Contains(5000, merged.SupportedCategories);
        Assert.Contains(2000, merged.SupportedCategories);
    }

    [Fact]
    public void Merge_NeverAdvertisesBookCategory_WhenBookIsTheOnlySourceCategory()
    {
        var onlyBookCaps = new SourceCaps(new[] { 7000 }, false, false, 100);

        var merged = CapsAggregator.Merge(new[] { onlyBookCaps });

        Assert.Empty(merged.SupportedCategories);
    }

    // ---------- SupportedParams: intersection ----------

    [Fact]
    public void Merge_IntersectsSupportedParamsAcrossSources()
    {
        var capsA = new SourceCaps(new[] { 5000 }, true, false, 50, SupportedParams: new[] { "q", "season", "ep", "imdbid" });
        var capsB = new SourceCaps(new[] { 5000 }, true, false, 50, SupportedParams: new[] { "q", "season" });

        var merged = CapsAggregator.Merge(new[] { capsA, capsB });

        Assert.Equal(new[] { "q", "season" }, merged.SupportedParams!.OrderBy(p => p));
        Assert.DoesNotContain("ep", merged.SupportedParams!);
        Assert.DoesNotContain("imdbid", merged.SupportedParams!);
    }

    [Fact]
    public void Merge_SupportedParams_EmptyWhenOneSourceAdvertisesNone()
    {
        var capsA = new SourceCaps(new[] { 5000 }, true, false, 50, SupportedParams: new[] { "q", "season" });
        var capsB = new SourceCaps(new[] { 5000 }, true, false, 50, SupportedParams: Array.Empty<string>());

        var merged = CapsAggregator.Merge(new[] { capsA, capsB });

        Assert.Empty(merged.SupportedParams!);
    }

    // ---------- MaxPageSize: our own enforced value, not a passthrough ----------

    [Theory]
    [InlineData(500)]
    [InlineData(10)]
    [InlineData(null)]
    public void Merge_AlwaysEnforcesMaxPageSizeOf100_RegardlessOfUpstreamValue(int? upstreamMax)
    {
        var caps = new SourceCaps(new[] { 5000 }, true, false, upstreamMax);

        var merged = CapsAggregator.Merge(new[] { caps });

        Assert.Equal(100, merged.MaxPageSize);
    }

    // ---------- Last-known-good fallback: genuinely exercises the failure path ----------

    private sealed class InMemoryCapsCacheStore : ICapsCacheStore
    {
        private readonly Dictionary<string, SourceCaps> _store = new();

        public Task<SourceCaps?> GetLastKnownGoodAsync(string sourceName, CancellationToken cancellationToken = default)
            => Task.FromResult(_store.TryGetValue(sourceName, out var caps) ? caps : null);

        public Task SaveAsync(string sourceName, SourceCaps caps, CancellationToken cancellationToken = default)
        {
            _store[sourceName] = caps;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpstreamSource : IUpstreamSource
    {
        private readonly Func<Task<SourceCaps>> _getCaps;

        public FakeUpstreamSource(string name, Func<Task<SourceCaps>> getCaps)
        {
            Name = name;
            _getCaps = getCaps;
        }

        public string Name { get; }

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SourceCaps> GetCapsAsync(CancellationToken cancellationToken = default) => _getCaps();

        public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task AggregateAsync_FallsBackToLastKnownGood_WhenSourceFailsOnSubsequentFetch()
    {
        var cacheStore = new InMemoryCapsCacheStore();
        var aggregator = new CapsAggregator(cacheStore);

        var goodCaps = new SourceCaps(
            SupportedCategories: new[] { 5000, 5030 },
            SupportsTvSearch: true,
            SupportsMovieSearch: false,
            MaxPageSize: 50,
            SupportedParams: new[] { "q", "season" },
            SupportsAnimeSearch: true);

        // Step (a): the source succeeds once — this both returns real caps AND causes the
        // aggregator to cache them as last-known-good.
        var callCount = 0;
        var flakySource = new FakeUpstreamSource("hydra-1", () =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(goodCaps);
            }

            // Step (b): every subsequent fetch fails — simulates the upstream going down.
            throw new HttpRequestException("simulated upstream failure");
        });

        var firstResult = await aggregator.AggregateAsync(new[] { flakySource });
        Assert.Equal(1, callCount);
        Assert.Contains(5000, firstResult.SupportedCategories);

        // Step (c): the second aggregation call hits the failure path (callCount becomes 2,
        // proving GetCapsAsync was actually invoked and actually threw) — assert the merge
        // still reflects the source's last-known-good caps, not empty/default caps.
        var secondResult = await aggregator.AggregateAsync(new[] { flakySource });

        Assert.Equal(2, callCount); // proves the failure path was genuinely exercised, not skipped
        Assert.Contains(5000, secondResult.SupportedCategories);
        Assert.Contains(5030, secondResult.SupportedCategories);
        Assert.True(secondResult.SupportsTvSearch);
        Assert.True(secondResult.SupportsAnimeSearch);
        Assert.NotEmpty(secondResult.SupportedParams!);
    }

    [Fact]
    public async Task AggregateAsync_SourceWithNoCacheAndFailingFetch_ContributesNothing_ButDoesNotThrow()
    {
        var cacheStore = new InMemoryCapsCacheStore();
        var aggregator = new CapsAggregator(cacheStore);

        var alwaysFailingSource = new FakeUpstreamSource(
            "never-worked",
            () => throw new TimeoutException("simulated timeout, never succeeded, nothing cached"));

        var result = await aggregator.AggregateAsync(new[] { alwaysFailingSource });

        Assert.Empty(result.SupportedCategories);
        Assert.Equal(100, result.MaxPageSize);
    }

    [Fact]
    public async Task AggregateAsync_HealthySourcePlusFailedSourceWithLastKnownGood_MergesBoth()
    {
        var cacheStore = new InMemoryCapsCacheStore();
        var aggregator = new CapsAggregator(cacheStore);

        // Pre-seed last-known-good caps for a source that will fail on this fetch cycle.
        var deadSourceCachedCaps = new SourceCaps(new[] { 2000 }, false, true, 40);
        await cacheStore.SaveAsync("dead-source", deadSourceCachedCaps);

        var deadSource = new FakeUpstreamSource(
            "dead-source",
            () => throw new HttpRequestException("simulated: currently unreachable"));

        var healthySource = new FakeUpstreamSource(
            "healthy-source",
            () => Task.FromResult(new SourceCaps(new[] { 5000 }, true, false, 50)));

        var merged = await aggregator.AggregateAsync(new[] { deadSource, healthySource });

        Assert.Contains(2000, merged.SupportedCategories); // from dead source's last-known-good
        Assert.Contains(5000, merged.SupportedCategories); // from the healthy source
    }
}
