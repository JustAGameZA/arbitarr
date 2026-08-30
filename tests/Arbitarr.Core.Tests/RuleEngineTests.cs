using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Microsoft.Extensions.Time.Testing;

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

    /// <summary>
    /// A rule that, when evaluated, advances a shared <see cref="FakeTimeProvider"/>'s clock — used
    /// to simulate wall-clock elapsing across an aggregate evaluation without relying on real time.
    /// </summary>
    private sealed class ClockAdvancingRule : IFilterRule
    {
        private readonly FakeTimeProvider _timeProvider;
        private readonly TimeSpan _advanceBy;

        public ClockAdvancingRule(string name, FakeTimeProvider timeProvider, TimeSpan advanceBy)
        {
            Name = name;
            _timeProvider = timeProvider;
            _advanceBy = advanceBy;
        }

        public string Name { get; }

        public Precedence Precedence => Precedence.Normal;

        public bool IsAllow => false;

        public int TimesEvaluated { get; private set; }

        public Verdict Evaluate(ReleaseCandidate candidate)
        {
            TimesEvaluated++;
            _timeProvider.Advance(_advanceBy);
            return Verdict.Unknown;
        }
    }

    /// <summary>
    /// M4 security review (MEDIUM): unbounded aggregate evaluation time. A profile with many rules,
    /// each individually fast, can still take arbitrarily long in aggregate; <see cref="RuleEvaluator"/>
    /// must cut evaluation short once <see cref="FilterProfile.TotalEvaluationBudget"/> is exceeded,
    /// leaving the remaining rules unevaluated, and fail open (default-allow) rather than reject the
    /// candidate purely because the budget ran out.
    /// </summary>
    [Fact]
    public void Evaluate_AggregateBudgetExceeded_StopsEvaluatingRemainingRules_AndFailsOpen()
    {
        var timeProvider = new FakeTimeProvider();
        // Each rule "costs" 30ms of simulated wall-clock time. The budget is checked BEFORE each
        // rule runs: rule-1 runs at 0ms elapsed (< 50ms), rule-2 runs at 30ms elapsed (< 50ms), and
        // rule-3 is skipped because by then elapsed is 60ms (>= 50ms budget).
        var rule1 = new ClockAdvancingRule("rule-1", timeProvider, TimeSpan.FromMilliseconds(30));
        var rule2 = new ClockAdvancingRule("rule-2", timeProvider, TimeSpan.FromMilliseconds(30));
        var rule3 = new ClockAdvancingRule("rule-3", timeProvider, TimeSpan.FromMilliseconds(30));
        var profile = new FilterProfile(
            "budget-test",
            new IFilterRule[] { rule1, rule2, rule3 },
            totalEvaluationBudget: TimeSpan.FromMilliseconds(50));

        var (verdict, matchedRule) = RuleEvaluator.Evaluate(
            profile,
            Candidate("Some.Release.Title"),
            timeProvider);

        // Fail open: no rule matched (all three, had they run, return Unknown anyway) so the
        // candidate defaults to Accept — a budget cutoff must never itself produce a Reject.
        Assert.Equal(Verdict.Accept, verdict);
        Assert.Null(matchedRule);

        // The budget cut evaluation short: rule-1 and rule-2 ran (within budget), but rule-3 did
        // not, proving remaining candidates/rules are skipped rather than evaluated regardless of cost.
        Assert.Equal(1, rule1.TimesEvaluated);
        Assert.Equal(1, rule2.TimesEvaluated);
        Assert.Equal(0, rule3.TimesEvaluated);
    }

    /// <summary>
    /// Sanity check that a generous budget does not interfere with normal evaluation: every rule
    /// runs and a deny match still wins, proving the budget is additive to (not a replacement for)
    /// existing precedence/tie-break semantics.
    /// </summary>
    [Fact]
    public void Evaluate_WithinBudget_AllRulesRun_NormalPrecedenceApplies()
    {
        var timeProvider = new FakeTimeProvider();
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, "CAM"),
        });

        var (verdict, rule) = RuleEvaluator.Evaluate(
            profile,
            Candidate("Movie.Release.CAM"),
            timeProvider);

        Assert.Equal(Verdict.Reject, verdict);
        Assert.Equal("deny-cam", rule?.Name);
    }
}
