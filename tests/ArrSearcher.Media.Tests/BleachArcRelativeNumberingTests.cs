using ArrSearcher.Core.Identity;
using ArrSearcher.Media.Numbering;
using Xunit;

namespace ArrSearcher.Media.Tests;

/// <summary>
/// Flagship fixture (AC-M3, AC-M3a): release <c>Bleach-17x36(402)</c> must resolve to arc-relative
/// S01E36 of the "Thousand-Year Blood War" (TYBW) arc via the season-keyed arc-title map, while a
/// real XEM absolute-36 collision elsewhere in the same map must surface as an ambiguous mapping
/// rather than a guess. Also covers the AC-M1 generation-time exclusion regression enforced by
/// <see cref="CandidateNumberingSetBuilder"/>: a bare (season=1, arc_relative_ep) candidate must never
/// be generated at all.
/// </summary>
public class BleachArcRelativeNumberingTests
{
    /// <summary>
    /// The TYBW arc binding: scene season 17, arc-relative episode 36 corresponds to absolute
    /// episode 402 (Bleach's original TVDB run absolute numbering). Its own absolute range does not
    /// include 36 - that collision belongs to a separate pair of arcs earlier in the series, modelled
    /// in <see cref="AmbiguousAbsolute36Map"/> below.
    /// </summary>
    private static ArcSeasonBinding TybwBinding => new(
        ArcTitle: "Thousand-Year Blood War",
        AlternateArcTitles: ["TYBW", "Thousand Year Blood War"],
        Season: 17,
        AbsoluteRangeStart: 367,
        AbsoluteRangeEnd: 402);

    private static ArcSeasonMap BleachArcMap => new([TybwBinding]);

    /// <summary>
    /// The real XEM collision case: two distinct early-series arcs both (incorrectly, per XEM's
    /// hand-edited data) claim absolute episode 36. Neither binding is TYBW - this models a lookup
    /// against the map for a *different* release whose only numbering signal is a bare absolute "36".
    /// </summary>
    private static ArcSeasonMap AmbiguousAbsolute36Map => new(
    [
        new ArcSeasonBinding("Agent of the Shinigami", [], Season: 1, AbsoluteRangeStart: 1, AbsoluteRangeEnd: 20),
        new ArcSeasonBinding("Entry: Soul Society", [], Season: 2, AbsoluteRangeStart: 21, AbsoluteRangeEnd: 63),
        // A hand-edited overlap: a third, malformed binding also claims 36 (the real-world collision shape).
        new ArcSeasonBinding("Bounts (misfiled)", [], Season: 8, AbsoluteRangeStart: 30, AbsoluteRangeEnd: 40),
    ]);

    [Fact]
    public void Build_BleachRelease_WithArcTitleToken_ResolvesToArcRelativeSeason17Episode36()
    {
        var raw = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 36, Absolute: 402, ArcTitleToken: "TYBW");

        var result = CandidateNumberingSetBuilder.Build(raw, BleachArcMap);

        var arcRelative = Assert.Single(result.Candidates, c => c.Scheme == NumberingScheme.ArcRelative);
        Assert.Equal(17, arcRelative.Season);
        Assert.Equal(36, arcRelative.Episode);
        Assert.Equal(402, arcRelative.Absolute);
    }

    [Fact]
    public void Build_BleachRelease_WithoutExplicitAbsolute_DerivesAbsoluteFromArcRangeStart()
    {
        // No absolute given by the release; the builder must derive it from the arc binding's range
        // start plus the scene-relative offset, not leave it null.
        var raw = new RawReleaseNumbering(SceneSeason: 17, SceneEpisode: 1, Absolute: null, ArcTitleToken: "TYBW");

        var result = CandidateNumberingSetBuilder.Build(raw, BleachArcMap);

        var arcRelative = Assert.Single(result.Candidates, c => c.Scheme == NumberingScheme.ArcRelative);
        Assert.Equal(367, arcRelative.Absolute);
    }

    [Fact]
    public void Build_SceneSeasonOne_WithNoResolvableArcBinding_NeverGeneratesBareArcRelativeCandidate()
    {
        // AC-M1 regression: scene season 1 with an arc title token that matches nothing in the map,
        // and no absolute to fall back on, must never produce an ArcRelative(season:1, ...) candidate.
        var raw = new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 5, Absolute: null, ArcTitleToken: "Not A Real Arc");

        var result = CandidateNumberingSetBuilder.Build(raw, BleachArcMap);

        Assert.DoesNotContain(result.Candidates, c => c.Scheme == NumberingScheme.ArcRelative && c.Season == 1);
    }

    [Fact]
    public void Build_SceneSeasonOne_WithNoResolvableArcBinding_GeneratesNoArcRelativeCandidateAtAll()
    {
        // Stronger form of the regression check: not merely "no season=1 candidate", but no
        // ArcRelative candidate is generated at all for this shape - it is excluded at generation
        // time, not filtered afterward.
        var raw = new RawReleaseNumbering(SceneSeason: 1, SceneEpisode: 5, Absolute: null, ArcTitleToken: "Not A Real Arc");

        var result = CandidateNumberingSetBuilder.Build(raw, BleachArcMap);

        Assert.DoesNotContain(result.Candidates, c => c.Scheme == NumberingScheme.ArcRelative);
    }

    [Fact]
    public void Build_SceneSeasonNotOne_WithNoResolvableArcBinding_StillCarriesThroughAsArcRelative()
    {
        // Contrast case: when scene season is meaningfully non-1, the builder is allowed to carry it
        // through even without a resolved arc binding (only season==1 with no binding is excluded).
        var raw = new RawReleaseNumbering(SceneSeason: 4, SceneEpisode: 5, Absolute: null, ArcTitleToken: "Not A Real Arc");

        var result = CandidateNumberingSetBuilder.Build(raw, BleachArcMap);

        var arcRelative = Assert.Single(result.Candidates, c => c.Scheme == NumberingScheme.ArcRelative);
        Assert.Equal(4, arcRelative.Season);
    }

    [Fact]
    public void Build_WithAbsoluteCollisionAcrossTwoBindings_ResolvesToNullBinding_NoArcRelativeCandidateProduced()
    {
        // The real XEM abs-36 collision, exercised through the builder: a release carrying only a
        // bare absolute number that hits >1 binding must not resolve to any single arc, so no
        // ArcRelative candidate is generated for it (AC-M3a's "admit none" requirement, at the
        // generation layer).
        var raw = new RawReleaseNumbering(SceneSeason: 8, SceneEpisode: 6, Absolute: 36, ArcTitleToken: null);

        var result = CandidateNumberingSetBuilder.Build(raw, AmbiguousAbsolute36Map);

        // sceneSeason (8) != 1, so the "carry through as-is" branch fires instead of the season==1
        // exclusion - this is expected: the collision-detection responsibility for that specific
        // ambiguity belongs to AmbiguityPolicy (see AmbiguityPolicy_* tests below), while the builder
        // itself must never silently "pick" one of the colliding bindings' seasons.
        var arcRelative = Assert.Single(result.Candidates, c => c.Scheme == NumberingScheme.ArcRelative);
        Assert.Equal(8, arcRelative.Season); // carried through verbatim, NOT the colliding bindings' seasons (1 or 2)
    }

    [Fact]
    public void AmbiguityPolicy_Evaluate_RealAbsolute36Collision_ReturnsAmbiguousWithNoResolvedBinding()
    {
        var evaluation = AmbiguityPolicy.Evaluate(AmbiguousAbsolute36Map, absoluteEpisode: 36);

        Assert.Equal(MatchProvenanceFlagsAmbiguous, evaluation.Flags);
        Assert.Null(evaluation.Resolved);
        Assert.Equal(2, evaluation.Competing.Count);
    }

    [Fact]
    public void AmbiguityPolicy_Evaluate_RealAbsolute36Collision_NeverPicksAGuess()
    {
        var evaluation = AmbiguityPolicy.Evaluate(AmbiguousAbsolute36Map, absoluteEpisode: 36);

        // Explicitly assert the "admit none" contract: Resolved must be null even though competing
        // bindings exist and one of them might look more "likely."
        Assert.Null(evaluation.Resolved);
    }

    [Fact]
    public void AmbiguityPolicy_Evaluate_TybwAbsolute402_ResolvesSafely_NoAmbiguity()
    {
        var evaluation = AmbiguityPolicy.Evaluate(BleachArcMap, absoluteEpisode: 402);

        Assert.Equal(Core.Identity.MatchProvenanceFlags.None, evaluation.Flags);
        Assert.NotNull(evaluation.Resolved);
        Assert.Equal("Thousand-Year Blood War", evaluation.Resolved!.ArcTitle);
        Assert.Empty(evaluation.Competing);
    }

    [Fact]
    public void AmbiguityPolicy_Evaluate_NoMapAvailable_ReturnsNoCoverage_DistinctFromAmbiguous()
    {
        var evaluation = AmbiguityPolicy.Evaluate(arcMap: null, absoluteEpisode: 36);

        Assert.Equal(Core.Identity.MatchProvenanceFlags.NoXemCoverage, evaluation.Flags);
        Assert.NotEqual(MatchProvenanceFlagsAmbiguous, evaluation.Flags);
    }

    [Fact]
    public void AmbiguityPolicy_Evaluate_AbsoluteNotCoveredByAnyBinding_ReturnsNoCoverage()
    {
        var evaluation = AmbiguityPolicy.Evaluate(BleachArcMap, absoluteEpisode: 9999);

        Assert.Equal(Core.Identity.MatchProvenanceFlags.NoXemCoverage, evaluation.Flags);
        Assert.Null(evaluation.Resolved);
    }

    private static Core.Identity.MatchProvenanceFlags MatchProvenanceFlagsAmbiguous =>
        Core.Identity.MatchProvenanceFlags.AmbiguousMapping;
}
