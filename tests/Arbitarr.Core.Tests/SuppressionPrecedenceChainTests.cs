using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves the D3 fixed chain shape (M4-6): allow-rule beats deny-rule beats the (stubbed) AI slot
/// beats pass-through, and that every rejection the chain produces surfaces as a
/// <see cref="SuppressionRecord"/> via <see cref="SuppressionPrecedenceChain.EvaluateBatch"/>
/// (M4-5: zero suppressions without a record).
/// </summary>
public class SuppressionPrecedenceChainTests
{
    private const double AiThreshold = 0.9;

    private static ReleaseCandidate Candidate(string title) => new()
    {
        Title = title,
        Guid = $"guid-{title}",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    [Fact]
    public void Evaluate_AllowRuleMatch_WinsOverDenyRule()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
            new FilterRule("allow-trusted", isAllow: true, Precedence.Highest, "CAM"),
        });

        var result = SuppressionPrecedenceChain.Evaluate(profile, Candidate("Movie.CAM"), AiThreshold);

        Assert.Equal(Verdict.Accept, result.Verdict);
        Assert.Equal(SuppressionSource.AllowRule, result.Source);
    }

    [Fact]
    public void Evaluate_DenyRuleMatch_WinsOverAiSlot_WhenNoAllowMatches()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
        });

        var result = SuppressionPrecedenceChain.Evaluate(profile, Candidate("Movie.CAM"), AiThreshold);

        Assert.Equal(Verdict.Reject, result.Verdict);
        Assert.Equal(SuppressionSource.DenyRule, result.Source);
        Assert.Equal("deny-cam", result.RuleName);
    }

    [Fact]
    public void Evaluate_NoRuleMatches_AiStubAbstains_PassesThrough()
    {
        var profile = new FilterProfile("default", Array.Empty<IFilterRule>());

        var result = SuppressionPrecedenceChain.Evaluate(profile, Candidate("Movie.WEB"), AiThreshold);

        Assert.Equal(Verdict.Accept, result.Verdict);
        Assert.Equal(SuppressionSource.Pass, result.Source);
    }

    [Fact]
    public void EvaluateBatch_EveryRejection_ProducesExactlyOneSuppressionRecord()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
        });
        var candidates = new[] { Candidate("Movie.CAM"), Candidate("Movie.WEB"), Candidate("Show.CAM") };

        var result = SuppressionPrecedenceChain.EvaluateBatch(profile, candidates, AiThreshold, "query", DateTimeOffset.UtcNow);

        var rejectedCount = candidates.Count(c => c.Title.Contains("CAM"));
        Assert.Equal(rejectedCount, result.Suppressions.Count);
        Assert.Equal(candidates.Length - rejectedCount, result.Survivors.Count);
        Assert.All(result.Suppressions, s => Assert.Contains("DenyRule", s.Reason));
    }
}
