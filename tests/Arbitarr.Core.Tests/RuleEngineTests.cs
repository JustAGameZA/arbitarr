using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves <see cref="RuleEngine"/>/<see cref="RuleEvaluator"/>'s core semantics: default-allow when
/// no rule matches, deny-wins-tie at the same <see cref="Precedence"/> tier, and higher precedence
/// wins across tiers regardless of allow/deny.
/// </summary>
public class RuleEngineTests
{
    private static ReleaseCandidate Candidate(string title) => new()
    {
        Title = title,
        Guid = $"guid-{title}",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    [Fact]
    public void Evaluate_NoRuleMatches_DefaultsToAccept()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-xyz", isAllow: false, Precedence.Normal, "xyz"),
        });
        var engine = new RuleEngine(profile);

        var result = engine.Evaluate(new[] { Candidate("Some.Release.Title") }, "query", DateTimeOffset.UtcNow);

        Assert.Single(result.Survivors);
        Assert.Empty(result.Suppressions);
    }

    [Fact]
    public void Evaluate_DenyRuleMatch_Suppresses_WithOneRecord()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
        });
        var engine = new RuleEngine(profile);

        var result = engine.Evaluate(new[] { Candidate("Movie.2024.CAM.x264") }, "query", DateTimeOffset.UtcNow);

        Assert.Empty(result.Survivors);
        Assert.Single(result.Suppressions);
        Assert.Contains("deny-cam", result.Suppressions[0].Reason);
    }

    [Fact]
    public void Evaluate_SameTierTie_DenyWinsOverAllow()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("allow-all", isAllow: true, Precedence.Normal, "Release"),
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
        });

        var (verdict, rule) = RuleEvaluator.Evaluate(profile, Candidate("Movie.Release.CAM"));

        Assert.Equal(Verdict.Reject, verdict);
        Assert.Equal("deny-cam", rule?.Name);
    }

    [Fact]
    public void Evaluate_HigherPrecedenceWins_AcrossTiers()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Low, "CAM"),
            new FilterRule("allow-trusted", isAllow: true, Precedence.Highest, "Release"),
        });

        var (verdict, rule) = RuleEvaluator.Evaluate(profile, Candidate("Movie.Release.CAM"));

        Assert.Equal(Verdict.Accept, verdict);
        Assert.Equal("allow-trusted", rule?.Name);
    }

    [Fact]
    public void DistinctProfiles_ProduceDistinctResultSets_ForSameCandidates_A3()
    {
        // A3: named API keys map to distinct filter profiles, so the same query can yield
        // different result sets depending on which profile the caller's key resolves to. Core has
        // no concept of "API key" itself (that mapping is owned by Arbitarr.Data outside Core,
        // AC6) — this proves the profile-level building block the mapping relies on.
        var strict = new RuleEngine(new FilterProfile("strict", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
        }));
        var permissive = new RuleEngine(new FilterProfile("permissive", Array.Empty<IFilterRule>()));

        var candidates = new[] { Candidate("Movie.CAM") };

        var strictResult = strict.Evaluate(candidates, "query", DateTimeOffset.UtcNow);
        var permissiveResult = permissive.Evaluate(candidates, "query", DateTimeOffset.UtcNow);

        Assert.Empty(strictResult.Survivors);
        Assert.Single(permissiveResult.Survivors);
    }
}
