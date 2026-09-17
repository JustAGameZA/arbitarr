using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves <see cref="CapsAggregator"/>'s merge semantics (AC5): union of categories
/// (including anime), with upstream category names preserved,
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

    // ---------- Categories: books excluded, every other upstream category and label preserved ----------

    /// <summary>
    /// arb-x7w8.5. Book categories are excluded UNCONDITIONALLY (CONTEXT.md "Caps aggregation"),
    /// which is the documented exception to the union rule — so an upstream advertising them is
    /// exactly the case that must not put them back.
    ///
    /// <para>Asserted PER CATEGORY ID rather than by comparing the whole array, and the two are not
    /// interchangeable. An array equality passes for the wrong reason the moment the expected array
    /// is edited to match a regression, and it says nothing about WHICH id was dropped; a per-id
    /// assertion names each excluded id and each surviving one, so a rule that started excluding
    /// 5000 too, or that only dropped the family's first member, fails on the specific id.</para>
    ///
    /// <para>The non-book ids are asserted PRESENT in the same test on purpose: an exclusion test
    /// whose merge produced nothing at all would pass every DoesNotContain vacuously. 5000 and 2000
    /// surviving is what proves the merge ran and had something to exclude FROM.</para>
    /// </summary>
    [Fact]
    public void Merge_ExcludesBookCategories_PerId_EvenWhenAnUpstreamAdvertisesThem()
    {
        var bookClaimingCaps = new SourceCaps(
            SupportedCategories: new[] { 5000, 7000, 7020, 7030, 7060 },
            SupportsTvSearch: true,
            SupportsMovieSearch: false,
            MaxPageSize: 100,
            CategoryNames: new Dictionary<int, string>
            {
                [5000] = "TV",
                [7000] = "Books",
                [7020] = "Books/EBook",
                [7030] = "Books/Comics",
                [7060] = "Books/Mags",
            });

        var otherCaps = new SourceCaps(
            SupportedCategories: new[] { 2000 },
            SupportsTvSearch: false,
            SupportsMovieSearch: true,
            MaxPageSize: 50,
            CategoryNames: new Dictionary<int, string> { [2000] = "Movies" });

        var merged = CapsAggregator.Merge(new[] { bookClaimingCaps, otherCaps });

        // Positive control first: the merge produced real categories, so the exclusions below are
        // assertions about a non-empty set rather than about nothing.
        Assert.Contains(5000, merged.SupportedCategories);
        Assert.Contains(2000, merged.SupportedCategories);

        // Per book id, every one the upstream advertised.
        Assert.DoesNotContain(7000, merged.SupportedCategories);
        Assert.DoesNotContain(7020, merged.SupportedCategories);
        Assert.DoesNotContain(7030, merged.SupportedCategories);
        Assert.DoesNotContain(7060, merged.SupportedCategories);

        // The NAMES go with the ids. A surviving name entry for an excluded category is how a
        // "category list filtered, label map forgotten" regression would surface — the caps XML
        // renders names from this map, so a leftover entry is a half-excluded category.
        Assert.DoesNotContain(7000, merged.CategoryNames!.Keys);
        Assert.DoesNotContain(7020, merged.CategoryNames.Keys);
        Assert.DoesNotContain(7030, merged.CategoryNames.Keys);
        Assert.DoesNotContain(7060, merged.CategoryNames.Keys);
        Assert.Equal("TV", merged.CategoryNames[5000]);
        Assert.Equal("Movies", merged.CategoryNames[2000]);
    }

    /// <summary>
    /// The exclusion is not a function of how many sources advertise the category. At N=3 with EVERY
    /// source advertising books, the union rule ("offered if ANY source supports it") would keep them
    /// most strongly — which is precisely the arrangement under which an implementation that had
    /// quietly become "exclude only if no source offers it" would still look correct.
    /// </summary>
    [Fact]
    public void Merge_ExcludesBookCategories_EvenWhenEverySourceAdvertisesThem()
    {
        var caps = Enumerable.Range(0, 3)
            .Select(_ => new SourceCaps(new[] { 5000, 7020 }, true, false, 100))
            .ToArray();

        var merged = CapsAggregator.Merge(caps);

        Assert.Contains(5000, merged.SupportedCategories); // positive control: the merge is non-empty
        Assert.DoesNotContain(7020, merged.SupportedCategories);
    }

    /// <summary>
    /// The bound is the FAMILY, so an id in it that nobody enumerated is excluded too, and the ids
    /// on either side of it are not. Asserted through <see cref="CapsAggregator.IsBookCategory"/>'s
    /// own constants so the test cannot drift from the implementation's bound while still passing.
    /// </summary>
    [Theory]
    [InlineData(6999, true)]   // just below the family — must survive
    [InlineData(7000, false)]  // inclusive lower bound
    [InlineData(7045, false)]  // an id nobody wrote down, inside the family
    [InlineData(7999, false)]  // last id in the family
    [InlineData(8000, true)]   // exclusive upper bound — must survive
    public void Merge_BookExclusionCoversTheWholeFamilyAndNothingOutsideIt(int categoryId, bool expectedToSurvive)
    {
        // A second, always-surviving category so a merge that dropped everything cannot make the
        // "excluded" half of this theory pass vacuously.
        var merged = CapsAggregator.Merge(new[] { new SourceCaps(new[] { 5000, categoryId }, true, false, 100) });

        Assert.Contains(5000, merged.SupportedCategories);
        Assert.Equal(expectedToSurvive, merged.SupportedCategories.Contains(categoryId));
        Assert.Equal(expectedToSurvive, CapsAggregator.IsBookCategory(categoryId) is false);
    }

    [Fact]
    public void Merge_PreservesFirstConfiguredSourceNamesForNonBookCategories()
    {
        var first = new SourceCaps(
            SupportedCategories: new[] { 5000 },
            SupportsTvSearch: true,
            SupportsMovieSearch: false,
            MaxPageSize: 100,
            CategoryNames: new Dictionary<int, string> { [5000] = "TV" });

        var second = new SourceCaps(
            SupportedCategories: new[] { 2000 },
            SupportsTvSearch: false,
            SupportsMovieSearch: true,
            MaxPageSize: 50,
            CategoryNames: new Dictionary<int, string> { [2000] = "Movies" });

        var merged = CapsAggregator.Merge(new[] { first, second });

        Assert.Equal(new[] { 2000, 5000 }, merged.SupportedCategories);
        Assert.Equal("TV", merged.CategoryNames![5000]);
        Assert.Equal("Movies", merged.CategoryNames[2000]);
    }

    [Fact]
    public void Merge_UsesFirstConfiguredSourceName_WhenSourcesDisagree()
    {
        var first = new SourceCaps(
            SupportedCategories: new[] { 5040 },
            SupportsTvSearch: true,
            SupportsMovieSearch: false,
            MaxPageSize: 100,
            CategoryNames: new Dictionary<int, string> { [5040] = "TV/HD" });
        var second = new SourceCaps(
            SupportedCategories: new[] { 5040 },
            SupportsTvSearch: true,
            SupportsMovieSearch: false,
            MaxPageSize: 100,
            CategoryNames: new Dictionary<int, string> { [5040] = "HD TV" });

        var merged = CapsAggregator.Merge(new[] { first, second });

        Assert.Equal("TV/HD", merged.CategoryNames![5040]);
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

        // Mirrors the real store: removes the several protocol keys the bare name expands into,
        // never a prefix match, so a double is not kinder than the thing it stands in for.
        public Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default)
        {
            foreach (var protocol in CapsAggregator.AllProtocols)
            {
                _store.Remove(CapsAggregator.CacheKey(sourceName, protocol));
            }

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

        public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default) => _getCaps();

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

        var firstResult = await aggregator.AggregateAsync(new[] { flakySource }, SearchProtocol.Torznab);
        Assert.Equal(1, callCount);
        Assert.Contains(5000, firstResult.SupportedCategories);

        // Step (c): the second aggregation call hits the failure path (callCount becomes 2,
        // proving GetCapsAsync was actually invoked and actually threw) — assert the merge
        // still reflects the source's last-known-good caps, not empty/default caps.
        var secondResult = await aggregator.AggregateAsync(new[] { flakySource }, SearchProtocol.Torznab);

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

        var result = await aggregator.AggregateAsync(new[] { alwaysFailingSource }, SearchProtocol.Torznab);

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
        // Seeded under the aggregator's own key convention (#99 scopes it by protocol). Built via
        // CapsAggregator.CacheKey rather than hand-formatted, so a change to the convention cannot
        // leave this row unfindable while the test still passes vacuously.
        await cacheStore.SaveAsync(
            CapsAggregator.CacheKey("dead-source", SearchProtocol.Torznab), deadSourceCachedCaps);

        var deadSource = new FakeUpstreamSource(
            "dead-source",
            () => throw new HttpRequestException("simulated: currently unreachable"));

        var healthySource = new FakeUpstreamSource(
            "healthy-source",
            () => Task.FromResult(new SourceCaps(new[] { 5000 }, true, false, 50)));

        var merged = await aggregator.AggregateAsync(new[] { deadSource, healthySource }, SearchProtocol.Torznab);

        Assert.Contains(2000, merged.SupportedCategories); // from dead source's last-known-good
        Assert.Contains(5000, merged.SupportedCategories); // from the healthy source
    }

    // ---------- The fallback AT N=3, which is where it has never been exercised ----------

    /// <summary>
    /// arb-x7w8.5. The last-known-good fallback has only ever been proved at N=1 and N=2, where
    /// "does not narrow the merge" cannot distinguish a working fallback from a merge that happens
    /// to be dominated by the one healthy source. At N=3 it can.
    ///
    /// <para><b>Asserted PER FIELD, because the two merge rules fail in OPPOSITE directions and a
    /// single "the result looks right" assertion catches neither.</b> If the failed source were
    /// DROPPED rather than backed by its cache, the union would LOSE its categories (too few) while
    /// the intersection would GAIN params the dropped source did not support (too many) — so a test
    /// asserting only categories passes while the param rule is broken, and vice versa. Both are
    /// asserted here, each in the direction its own rule fails.</para>
    ///
    /// <para>The arrangement is chosen so each assertion can only be satisfied one way: category 3000
    /// exists ONLY in the failed source's cached entry, so its presence proves the cached entry
    /// reached the merge and nothing else could have put it there. Param "ep" is supported by both
    /// healthy sources and NOT by the failed one, so its ABSENCE proves the cached entry participated
    /// in the intersection — dropping the failed source entirely is exactly what would let "ep"
    /// through.</para>
    /// </summary>
    [Fact]
    public async Task AggregateAsync_AtThreeSources_OneFailingWithCache_NarrowsNeitherRule()
    {
        var cacheStore = new InMemoryCapsCacheStore();
        var aggregator = new CapsAggregator(cacheStore);

        // The failing source's last-known-good: the ONLY holder of category 3000, and it does not
        // support "ep".
        await cacheStore.SaveAsync(
            CapsAggregator.CacheKey("dead-source", SearchProtocol.Torznab),
            new SourceCaps(
                SupportedCategories: new[] { 2000, 3000 },
                SupportsTvSearch: false,
                SupportsMovieSearch: true,
                MaxPageSize: 40,
                SupportedParams: new[] { "q", "season" },
                SupportsAnimeSearch: true));

        var deadSource = new FakeUpstreamSource(
            "dead-source",
            () => throw new HttpRequestException("simulated: currently unreachable"));

        var healthyOne = new FakeUpstreamSource(
            "healthy-1",
            () => Task.FromResult(new SourceCaps(
                SupportedCategories: new[] { 5000 },
                SupportsTvSearch: true,
                SupportsMovieSearch: false,
                MaxPageSize: 50,
                SupportedParams: new[] { "q", "season", "ep" })));

        var healthyTwo = new FakeUpstreamSource(
            "healthy-2",
            () => Task.FromResult(new SourceCaps(
                SupportedCategories: new[] { 5030 },
                SupportsTvSearch: true,
                SupportsMovieSearch: false,
                MaxPageSize: 50,
                SupportedParams: new[] { "q", "season", "ep", "imdbid" })));

        var merged = await aggregator.AggregateAsync(
            new IUpstreamSource[] { deadSource, healthyOne, healthyTwo }, SearchProtocol.Torznab);

        // FIELD 1 — categories, still UNIONED across all three. 3000 is unobtainable from either
        // healthy source, so it can only have come from the failed source's cached entry.
        Assert.Contains(3000, merged.SupportedCategories);
        Assert.Contains(2000, merged.SupportedCategories);
        Assert.Contains(5000, merged.SupportedCategories);
        Assert.Contains(5030, merged.SupportedCategories);

        // FIELD 2 — params, still INTERSECTED across all three. "ep" is in both healthy sources and
        // absent from the cached entry; letting it through is the signature of the failed source
        // having been dropped from the merge rather than backed by its cache.
        Assert.Equal(new[] { "q", "season" }, merged.SupportedParams!.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("ep", merged.SupportedParams!);
        Assert.DoesNotContain("imdbid", merged.SupportedParams!);

        // FIELD 3 — the union-semantics booleans, each reachable from a different source, so all
        // three participated rather than one dominating.
        Assert.True(merged.SupportsTvSearch);       // healthy sources
        Assert.True(merged.SupportsMovieSearch);    // cached entry only
        Assert.True(merged.SupportsAnimeSearch);    // cached entry only

        // FIELD 4 — our own enforced page size, not any of the three upstream values (40/50/50).
        Assert.Equal(CapsAggregator.EnforcedMaxPageSize, merged.MaxPageSize);
    }

    /// <summary>
    /// arb-x7w8.5. The other half of the N=3 case: a source that fails AND has never cached anything
    /// contributes nothing, but does not drag the merge to empty.
    ///
    /// <para>Contributing "nothing" has a specific meaning for the INTERSECTION that the categories
    /// assertion cannot see. A no-cache source treated as contributing an EMPTY param list would
    /// intersect the merged params down to nothing — the merge would not be empty, but every
    /// id-search param would silently vanish, which is the narrowing this rule exists to prevent. So
    /// the params are asserted to be exactly the two healthy sources' intersection, unaffected by the
    /// third source's existence.</para>
    /// </summary>
    [Fact]
    public async Task AggregateAsync_AtThreeSources_OneFailingWithNoCache_ContributesNothingAndNarrowsNothing()
    {
        var cacheStore = new InMemoryCapsCacheStore();
        var aggregator = new CapsAggregator(cacheStore);

        var neverWorked = new FakeUpstreamSource(
            "never-worked",
            () => throw new TimeoutException("simulated timeout, never succeeded, nothing cached"));

        var healthyOne = new FakeUpstreamSource(
            "healthy-1",
            () => Task.FromResult(new SourceCaps(
                SupportedCategories: new[] { 5000 },
                SupportsTvSearch: true,
                SupportsMovieSearch: false,
                MaxPageSize: 50,
                SupportedParams: new[] { "q", "season", "ep" })));

        var healthyTwo = new FakeUpstreamSource(
            "healthy-2",
            () => Task.FromResult(new SourceCaps(
                SupportedCategories: new[] { 2000 },
                SupportsTvSearch: false,
                SupportsMovieSearch: true,
                MaxPageSize: 50,
                SupportedParams: new[] { "q", "season", "ep", "imdbid" })));

        var merged = await aggregator.AggregateAsync(
            new IUpstreamSource[] { neverWorked, healthyOne, healthyTwo }, SearchProtocol.Torznab);

        // Not dragged to empty: both healthy sources are fully represented.
        Assert.Equal(new[] { 2000, 5000 }, merged.SupportedCategories.OrderBy(id => id));
        Assert.True(merged.SupportsTvSearch);
        Assert.True(merged.SupportsMovieSearch);

        // And the intersection is the TWO healthy sources' intersection — the third source did not
        // participate in it at all. "ep" surviving is what proves that: an empty contribution from
        // the never-worked source would have removed it.
        Assert.Equal(new[] { "ep", "q", "season" }, merged.SupportedParams!.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("imdbid", merged.SupportedParams!); // still intersected, not unioned
    }

    /// <summary>
    /// arb-x7w8.5. The failure path does not disturb the cached entry it fell back to: a second
    /// aggregation over the same still-down source produces the same answer, rather than the first
    /// fallback having overwritten the entry with what it returned (or with nothing).
    /// </summary>
    [Fact]
    public async Task AggregateAsync_FallbackDoesNotOverwriteTheCachedEntryItRead()
    {
        var cacheStore = new InMemoryCapsCacheStore();
        var aggregator = new CapsAggregator(cacheStore);

        var cacheKey = CapsAggregator.CacheKey("dead-source", SearchProtocol.Torznab);
        await cacheStore.SaveAsync(
            cacheKey,
            new SourceCaps(new[] { 2000, 3000 }, false, true, 40, SupportedParams: new[] { "q" }));

        var deadSource = new FakeUpstreamSource(
            "dead-source",
            () => throw new HttpRequestException("simulated: still unreachable"));

        var first = await aggregator.AggregateAsync(new[] { deadSource }, SearchProtocol.Torznab);
        var second = await aggregator.AggregateAsync(new[] { deadSource }, SearchProtocol.Torznab);

        Assert.Contains(3000, first.SupportedCategories);  // positive control: the fallback fired
        Assert.Equal(first.SupportedCategories, second.SupportedCategories);
        Assert.Equal(first.SupportedParams, second.SupportedParams);

        // And the stored row itself is untouched, which is the property rather than its symptom.
        var stored = await cacheStore.GetLastKnownGoodAsync(cacheKey);
        Assert.Equal(new[] { 2000, 3000 }, stored!.SupportedCategories);
    }
}
