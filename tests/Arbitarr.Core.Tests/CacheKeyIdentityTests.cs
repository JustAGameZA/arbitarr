using Arbitarr.Core.Identity;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves <see cref="SearchCacheKeyBuilder"/> satisfies both directions of AC23b(4)/M3-9: it must
/// collapse equivalent numbering renderings of the same episode onto one key, and it must separate
/// distinct series that share overlapping title text and numbering. "A key that only separates, or
/// only collapses, satisfies half of AC23b(4)" — so both directions are asserted here, never just one.
/// </summary>
public sealed class CacheKeyIdentityTests
{
    private static readonly SeriesIdentity Bleach = new(
        TvdbId: 74796,
        TmdbId: null,
        PrimaryTitle: "Bleach",
        AlternateTitles: new[] { "BLEACH" });

    [Fact]
    public void Build_CollapsesEquivalentNumberingRenderings_OntoOneKey()
    {
        // S17E36, 17x36, and 17x36 (402) are three release-text renderings of the same episode once
        // IEpisodeMatcher has resolved them all to the same arc-relative candidate: season 17,
        // episode 36, absolute 402. The key builder consumes the resolved candidate, not release
        // text, so all three inputs must produce byte-identical keys.
        var categories = new[] { 5000 };

        // Three independent NumberingCandidate instances (as if built from three different release
        // strings) that all resolved to the same season/episode/absolute/scheme.
        var fromS17E36 = new NumberingCandidate(NumberingScheme.ArcRelative, 17, 36, 402);
        var from17x36 = new NumberingCandidate(NumberingScheme.ArcRelative, 17, 36, 402);
        var from17x36WithAbsolute = new NumberingCandidate(NumberingScheme.ArcRelative, 17, 36, 402);

        var key1 = SearchCacheKeyBuilder.Build(Bleach, fromS17E36, categories);
        var key2 = SearchCacheKeyBuilder.Build(Bleach, from17x36, categories);
        var key3 = SearchCacheKeyBuilder.Build(Bleach, from17x36WithAbsolute, categories);

        Assert.Equal(key1, key2);
        Assert.Equal(key1, key3);
    }

    [Fact]
    public void Build_SeparatesDistinctSeries_WithOverlappingTitlesAndNumbering()
    {
        // Ghost in the Shell: Arise, Stand Alone Complex, and SAC_2045 share most of their title
        // tokens and can carry overlapping season/episode numbers, but are distinct, non-mergeable
        // works. Each carries its own TVDB ID once resolved -- exactly the disambiguation signal a
        // display-title-only key would lack.
        var arise = new SeriesIdentity(TvdbId: 269586, TmdbId: null, PrimaryTitle: "Ghost in the Shell: Arise", AlternateTitles: Array.Empty<string>());
        var sac = new SeriesIdentity(TvdbId: 78914, TmdbId: null, PrimaryTitle: "Ghost in the Shell: Stand Alone Complex", AlternateTitles: Array.Empty<string>());
        var sac2045 = new SeriesIdentity(TvdbId: 355730, TmdbId: null, PrimaryTitle: "Ghost in the Shell: SAC_2045", AlternateTitles: Array.Empty<string>());

        var sameNumbering = new NumberingCandidate(NumberingScheme.TvdbSeasonal, Season: 1, Episode: 1, Absolute: 1);
        var categories = new[] { 5000 };

        var ariseKey = SearchCacheKeyBuilder.Build(arise, sameNumbering, categories);
        var sacKey = SearchCacheKeyBuilder.Build(sac, sameNumbering, categories);
        var sac2045Key = SearchCacheKeyBuilder.Build(sac2045, sameNumbering, categories);

        Assert.NotEqual(ariseKey, sacKey);
        Assert.NotEqual(ariseKey, sac2045Key);
        Assert.NotEqual(sacKey, sac2045Key);
    }

    [Fact]
    public void Build_FallsBackToTitleSet_WhenNoProviderIdKnown()
    {
        // Arise's zero-result-fixture identity: no TVDB/TMDB ID resolved (upstream had no coverage),
        // so the key must fall back to the title set rather than colliding with every other
        // ID-less identity on a shared default token.
        var ariseUnresolved = new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: "Ghost in the Shell: Arise - Alternative Architecture", AlternateTitles: Array.Empty<string>());
        var otherUnresolved = new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: "Some Other Series", AlternateTitles: Array.Empty<string>());
        var numbering = new NumberingCandidate(NumberingScheme.TvdbSeasonal, 1, 1, 1);
        var categories = new[] { 5000 };

        var ariseKey = SearchCacheKeyBuilder.Build(ariseUnresolved, numbering, categories);
        var otherKey = SearchCacheKeyBuilder.Build(otherUnresolved, numbering, categories);

        Assert.NotEqual(ariseKey, otherKey);
    }

    [Fact]
    public void Build_SeparatesByCategory_ForOtherwiseIdenticalIdentityAndNumbering()
    {
        var numbering = new NumberingCandidate(NumberingScheme.TvdbSeasonal, 1, 1, 1);

        var tvKey = SearchCacheKeyBuilder.Build(Bleach, numbering, new[] { 5000 });
        var movieKey = SearchCacheKeyBuilder.Build(Bleach, numbering, new[] { 2000 });

        Assert.NotEqual(tvKey, movieKey);
    }

    [Fact]
    public void Build_CategoryOrderDoesNotAffectKey()
    {
        var numbering = new NumberingCandidate(NumberingScheme.TvdbSeasonal, 1, 1, 1);

        var key1 = SearchCacheKeyBuilder.Build(Bleach, numbering, new[] { 5000, 5030 });
        var key2 = SearchCacheKeyBuilder.Build(Bleach, numbering, new[] { 5030, 5000 });

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Build_TitleSetFallback_IsDeterministic_RegardlessOfOrderCasingOrDuplicates()
    {
        // Security-m3 HIGH #1: the title-set token is hashed, not embedded verbatim. Verify the
        // hash still collapses equivalent title sets (order-independent, case-insensitive,
        // duplicate-tolerant) rather than asserting the old raw "titles:{joined text}" format.
        var numbering = new NumberingCandidate(NumberingScheme.TvdbSeasonal, 1, 1, 1);
        var categories = new[] { 5000 };

        var canonical = new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: "Naruto", AlternateTitles: new[] { "NARUTO SHIPPUDEN" });
        var reorderedAndDuplicated = new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: "naruto shippuden", AlternateTitles: new[] { "  Naruto  ", "Naruto Shippuden" });

        var key1 = SearchCacheKeyBuilder.Build(canonical, numbering, categories);
        var key2 = SearchCacheKeyBuilder.Build(reorderedAndDuplicated, numbering, categories);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Build_TitleSetFallback_SeparatesDistinctTitleSets()
    {
        var numbering = new NumberingCandidate(NumberingScheme.TvdbSeasonal, 1, 1, 1);
        var categories = new[] { 5000 };

        var naruto = new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: "Naruto", AlternateTitles: Array.Empty<string>());
        var narutoShippuden = new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: "Naruto Shippuden", AlternateTitles: Array.Empty<string>());

        var key1 = SearchCacheKeyBuilder.Build(naruto, numbering, categories);
        var key2 = SearchCacheKeyBuilder.Build(narutoShippuden, numbering, categories);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Build_TitleSetFallback_StaysBounded_ForAVeryLongTitle()
    {
        // Security-m3 HIGH #1: an unbounded q-derived title must not produce an unbounded QueryKey
        // (one row per distinct value is a cache-table flooding vector). A 10 KB title should still
        // yield a key comfortably under ~200 characters once hashed.
        var hugeTitle = new string('a', 10_000);
        var identity = new SeriesIdentity(TvdbId: null, TmdbId: null, PrimaryTitle: hugeTitle, AlternateTitles: Array.Empty<string>());
        var numbering = new NumberingCandidate(NumberingScheme.TvdbSeasonal, 1, 1, 1);

        var key = SearchCacheKeyBuilder.Build(identity, numbering, new[] { 5000 });

        Assert.True(key.Length < 200, $"Expected a bounded key under 200 chars, got {key.Length}.");
    }

    [Fact]
    public void Build_DifferentNumberingScheme_SeparatesKeys_EvenWithSameSeasonEpisode()
    {
        // Arc-relative and TVDB-seasonal candidates can share the same bare season/episode numbers
        // while referring to different episodes (the Bleach absolute-numbering collision case) --
        // the scheme itself must be part of the key.
        var arcRelative = new NumberingCandidate(NumberingScheme.ArcRelative, 17, 36, 402);
        var tvdbSeasonal = new NumberingCandidate(NumberingScheme.TvdbSeasonal, 17, 36, null);
        var categories = new[] { 5000 };

        var key1 = SearchCacheKeyBuilder.Build(Bleach, arcRelative, categories);
        var key2 = SearchCacheKeyBuilder.Build(Bleach, tvdbSeasonal, categories);

        Assert.NotEqual(key1, key2);
    }
}
