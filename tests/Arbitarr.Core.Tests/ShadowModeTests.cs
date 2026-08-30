using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves both directions of AC12/D3: with shadow mode ON, suppressions are recorded but every
/// original candidate still survives; with shadow mode OFF, suppressions are enforced (survivors
/// shrink to the rule engine's own result).
/// </summary>
public class ShadowModeTests
{
    private static ReleaseCandidate Candidate(string title) => new()
    {
        Title = title,
        Guid = $"guid-{title}",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    private static (RuleEngineResult Result, IReadOnlyList<ReleaseCandidate> Original) BuildSuppressedResult()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
        });
        var engine = new RuleEngine(profile);
        var candidates = new[] { Candidate("Movie.CAM"), Candidate("Movie.WEB") };
        var result = engine.Evaluate(candidates, "query", DateTimeOffset.UtcNow);
        return (result, candidates);
    }

    [Fact]
    public void Apply_ShadowModeOn_RecordsSuppression_ButAllCandidatesSurvive()
    {
        var (result, original) = BuildSuppressedResult();
        Assert.Single(result.Suppressions); // sanity: the engine did suppress one candidate

        var gated = ShadowModeGate.Apply(original, result, shadowModeEnabled: true);

        Assert.Equal(original.Count, gated.EffectiveCandidates.Count);
        Assert.Single(gated.Suppressions);
        Assert.True(gated.Suppressions[0].ShadowMode);
    }

    [Fact]
    public void Apply_ShadowModeOff_EnforcesSuppression_SurvivorsMatchRuleEngine()
    {
        var (result, original) = BuildSuppressedResult();

        var gated = ShadowModeGate.Apply(original, result, shadowModeEnabled: false);

        Assert.Equal(result.Survivors.Count, gated.EffectiveCandidates.Count);
        Assert.Single(gated.Suppressions);
        Assert.False(gated.Suppressions[0].ShadowMode);
    }
}
