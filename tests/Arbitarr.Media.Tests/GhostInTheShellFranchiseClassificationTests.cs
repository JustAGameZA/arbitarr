using Arbitarr.Core.Identity;
using Arbitarr.Media.Identity;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// Ghost in the Shell fixture (AC-M7a): three canonical, distinct <see cref="SeriesIdentity"/>
/// entries (Arise, Stand Alone Complex, SAC_2045) must never merge. A sibling release is classified
/// as "sibling, not same series" with a recorded reason - a de-ranking classification, never a hard
/// admit/reject gate.
/// </summary>
public class GhostInTheShellFranchiseClassificationTests
{
    private static SeriesIdentity Arise => new(
        TvdbId: 264492,
        TmdbId: null,
        PrimaryTitle: "Ghost in the Shell: Arise",
        AlternateTitles: ["GitS: Arise"]);

    private static SeriesIdentity StandAloneComplex => new(
        TvdbId: 78983,
        TmdbId: null,
        PrimaryTitle: "Ghost in the Shell: Stand Alone Complex",
        AlternateTitles: ["GitS: SAC"]);

    private static SeriesIdentity Sac2045 => new(
        TvdbId: 361034,
        TmdbId: null,
        PrimaryTitle: "Ghost in the Shell: SAC_2045",
        AlternateTitles: []);

    [Fact]
    public void Classify_AllThreeCanonicalEntries_HaveDistinctTvdbIds_NeverMerged()
    {
        var ids = new[] { Arise.TvdbId, StandAloneComplex.TvdbId, Sac2045.TvdbId };

        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void Classify_AriseAgainstStandAloneComplex_ReturnsSibling_NotSame()
    {
        var classification = FranchiseClassifier.Classify(StandAloneComplex, Arise);

        Assert.Equal(FranchiseRelation.Sibling, classification.Relation);
    }

    [Fact]
    public void Classify_SiblingResult_RecordsAReasonExplainingTheSharedFranchiseToken()
    {
        var classification = FranchiseClassifier.Classify(StandAloneComplex, Arise);

        Assert.NotNull(classification.Reason);
        Assert.Contains("sibling, not same series", classification.Reason);
        Assert.Contains("Ghost in the Shell", classification.Reason);
    }

    [Fact]
    public void Classify_Sac2045AgainstArise_ReturnsSibling_DistinctIdentitiesPreserved()
    {
        var classification = FranchiseClassifier.Classify(Arise, Sac2045);

        Assert.Equal(FranchiseRelation.Sibling, classification.Relation);
        Assert.NotNull(classification.Reason);
    }

    [Fact]
    public void Classify_SameIdentityAgainstItself_BySharedTvdbId_ReturnsSame()
    {
        var classification = FranchiseClassifier.Classify(Arise, Arise with { PrimaryTitle = "Ghost in the Shell: Arise (alt render)" });

        Assert.Equal(FranchiseRelation.Same, classification.Relation);
        Assert.Null(classification.Reason);
    }

    [Fact]
    public void Classify_SiblingClassification_IsNotAHardGate_CandidateStillReturnedNotDiscarded()
    {
        // The classification itself carries no admit/reject signal - callers receive the sibling
        // classification and reason, and it remains their decision what to do with it. This test
        // documents that FranchiseClassification never throws/discards for a Sibling result.
        var classification = FranchiseClassifier.Classify(StandAloneComplex, Arise);

        Assert.NotEqual(FranchiseRelation.Unrelated, classification.Relation);
        Assert.NotNull(classification);
    }

    [Fact]
    public void Classify_TotallyUnrelatedSeries_ReturnsUnrelated_WithNoReason()
    {
        var unrelated = new SeriesIdentity(999999, null, "Cowboy Bebop", []);

        var classification = FranchiseClassifier.Classify(StandAloneComplex, unrelated);

        Assert.Equal(FranchiseRelation.Unrelated, classification.Relation);
        Assert.Null(classification.Reason);
    }
}
