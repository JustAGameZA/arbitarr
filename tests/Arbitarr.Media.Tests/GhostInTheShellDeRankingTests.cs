using System.Xml.Linq;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Identity.Scoring;
using Arbitarr.Media.Ranking;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// M6-2 (plan lines 831-845): the three canonical Ghost in the Shell series are distinct identities
/// that never merge, and a query for one of them ranks its own releases first. Franchise siblings
/// are de-ranked through <see cref="ScoringWeights.SiblingSeriesPenalty"/>, never dropped.
/// Titles come from the captured NZBHydra fixtures under docs/fixtures/nzbhydra/.
/// </summary>
/// <remarks>
/// The Arise fixture (<c>ghost-in-the-shell-arise-alternative-architecture.xml</c>) is an honest
/// zero-result capture, so the "own release ranks #1" criterion is vacuous for it; it is asserted
/// as a clean empty ranking instead, and the Arise identity is exercised as the requested series
/// over the generic fixture where every hit is a sibling and must be retained but de-ranked.
/// </remarks>
public class GhostInTheShellDeRankingTests
{
    private static SeriesIdentity Arise => new(264492, null, "Ghost in the Shell: Arise", ["GitS: Arise"]);
    private static SeriesIdentity StandAloneComplex => new(78983, null, "Ghost in the Shell: Stand Alone Complex", ["GitS: SAC"]);
    private static SeriesIdentity Sac2045 => new(361034, null, "Ghost in the Shell: SAC_2045", []);

    private static IReadOnlyList<SeriesIdentity> AllThree => [Arise, StandAloneComplex, Sac2045];

    private static RankingContext ContextFor(SeriesIdentity requested) => new(requested, AllThree, ArcMap: null);

    internal static IReadOnlyList<string> LoadFixtureTitles(string fixtureFileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureFileName);
        return XDocument.Load(path)
            .Descendants("item")
            .Select(item => (string?)item.Element("title"))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .ToArray();
    }

    private static bool IsSac2045Release(string title) =>
        title.Contains("SAC 2045", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("SAC2045", StringComparison.OrdinalIgnoreCase);

    private static bool IsStandAloneComplexRelease(string title) =>
        title.Contains("Stand Alone Complex", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ThreeCanonicalIdentities_AreNeverMerged()
    {
        Assert.Equal(3, AllThree.Select(i => i.TvdbId).Distinct().Count());
        Assert.Equal(3, AllThree.Select(i => i.PrimaryTitle).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void StandAloneComplexQuery_OverOwnFixture_RanksOwnReleaseFirst()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-stand-alone-complex.xml");
        Assert.NotEmpty(titles);

        var result = ReleaseRanking.Rank(titles, ContextFor(StandAloneComplex));

        Assert.Equal(titles.Count, result.Ranked.Count);
        Assert.True(IsStandAloneComplexRelease(result.Ranked[0].Release.Title));
        Assert.All(result.Ranked, r => Assert.Null(r.DeRankReason));
    }

    [Fact]
    public void StandAloneComplexQuery_OverGenericFixture_NoSac2045ReleaseRanksFirst()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-generic.xml");
        Assert.Contains(titles, IsSac2045Release);
        Assert.Contains(titles, IsStandAloneComplexRelease);

        var result = ReleaseRanking.Rank(titles, ContextFor(StandAloneComplex));

        var top = result.Ranked[0];
        Assert.True(IsStandAloneComplexRelease(top.Release.Title), $"expected an SAC release first, got: {top.Release.Title}");
        Assert.False(IsSac2045Release(top.Release.Title));

        // Every same-series release outranks every SAC_2045 sibling.
        var lowestSac = result.Ranked.Where(r => IsStandAloneComplexRelease(r.Release.Title)).Min(r => r.Confidence);
        var highestSibling = result.Ranked.Where(r => IsSac2045Release(r.Release.Title)).Max(r => r.Confidence);
        Assert.True(lowestSac > highestSibling, $"SAC min {lowestSac} must exceed SAC_2045 max {highestSibling}");
    }

    [Fact]
    public void StandAloneComplexQuery_SiblingsAreDeRankedNotDropped()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-generic.xml");

        var result = ReleaseRanking.Rank(titles, ContextFor(StandAloneComplex));

        Assert.Equal(titles.Count, result.Ranked.Count);

        var siblings = result.Ranked
            .Where(r => r.Release.Title.Contains("SAC 2045", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(siblings);
        Assert.All(siblings, r =>
        {
            Assert.Equal(ReleaseSeriesRelation.Sibling, r.Release.Relation);
            Assert.NotNull(r.DeRankReason);
            Assert.Contains("sibling, not same series", r.DeRankReason);
            Assert.False(r.MeetsAcceptanceThreshold);
        });
    }

    [Fact]
    public void SiblingPenalty_IsActuallyConsumed_FromScoringWeights()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-generic.xml");
        var sibling = titles.First(t => t.Contains("SAC 2045", StringComparison.OrdinalIgnoreCase));

        var penalised = ReleaseRanking.Rank([sibling], ContextFor(StandAloneComplex)).Ranked[0];
        var unpenalised = ReleaseRanking.Rank(
            [sibling],
            ContextFor(StandAloneComplex) with { Weights = new ScoringWeights { SiblingSeriesPenalty = 1.0 } }).Ranked[0];

        Assert.Equal(ReleaseSeriesRelation.Sibling, penalised.Release.Relation);
        Assert.True(penalised.Confidence < unpenalised.Confidence);
        Assert.Equal(unpenalised.Confidence * new ScoringWeights().SiblingSeriesPenalty, penalised.Confidence, precision: 10);
    }

    [Fact]
    public void Sac2045Query_OverGenericFixture_RanksOwnReleaseFirst()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-generic.xml");

        var result = ReleaseRanking.Rank(titles, ContextFor(Sac2045));

        Assert.True(IsSac2045Release(result.Ranked[0].Release.Title));
        Assert.Equal(ReleaseSeriesRelation.Same, result.Ranked[0].Release.Relation);
        Assert.All(
            result.Ranked.Where(r => IsStandAloneComplexRelease(r.Release.Title)),
            r => Assert.Equal(ReleaseSeriesRelation.Sibling, r.Release.Relation));
    }

    [Fact]
    public void AriseQuery_OverOwnFixture_ZeroResults_YieldsCleanEmptyRanking()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-arise-alternative-architecture.xml");
        Assert.Empty(titles);

        var result = ReleaseRanking.Rank(titles, ContextFor(Arise));

        Assert.Empty(result.Ranked);
    }

    [Fact]
    public void AriseQuery_OverGenericFixture_EverythingIsSiblingDeRanked_NothingDropped()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-generic.xml");

        var result = ReleaseRanking.Rank(titles, ContextFor(Arise));

        Assert.Equal(titles.Count, result.Ranked.Count);
        Assert.DoesNotContain(result.Ranked, r => r.Release.Relation == ReleaseSeriesRelation.Same);
        Assert.Contains(result.Ranked, r => r.Release.Relation == ReleaseSeriesRelation.Sibling);
        Assert.All(result.Ranked, r => Assert.False(r.MeetsAcceptanceThreshold));
    }

    [Fact]
    public void Ranking_IsOrderedByConfidenceDescending()
    {
        var titles = LoadFixtureTitles("ghost-in-the-shell-generic.xml");

        var result = ReleaseRanking.Rank(titles, ContextFor(StandAloneComplex));

        for (var i = 1; i < result.Ranked.Count; i++)
        {
            Assert.True(result.Ranked[i - 1].Confidence >= result.Ranked[i].Confidence);
        }
    }
}
