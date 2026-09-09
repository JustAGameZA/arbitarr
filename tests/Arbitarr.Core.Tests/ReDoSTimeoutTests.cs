using System.Diagnostics;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Tests;

/// <summary>
/// R11/P1: a catastrophic-backtracking (or otherwise pathological) regex pattern must time out and
/// be skipped ("did not match"), never stall the pipeline or crash the search. Uses a classic
/// exponential-backtracking pattern/input pair that a naive backtracking engine without a timeout
/// would hang on for a very long time.
/// </summary>
public class ReDoSTimeoutTests
{
    // Category=Timing (arb-rga.5) on this fact ALONE, not on the class. Only this one asserts
    // elapsed wall time (the Stopwatch bound below); Evaluate_BenignPattern_StillMatchesNormally is
    // a pure equality check and is the control proving the rule still matches at all, so excluding
    // it from the PR lane would cost coverage and buy no time back.
    [Trait("Category", "Timing")]
    [Fact]
    public void Evaluate_CatastrophicPattern_TimesOutAndReturnsUnknown_WithoutStalling()
    {
        // NonBacktracking would normally make this pattern linear-time, so exercise a construct
        // NonBacktracking rejects (a backreference) to force the backtracking-engine fallback path,
        // where MatchTimeout is the only ReDoS guard.
        var rule = new FilterRule("hazard", isAllow: false, Precedence.Normal, @"(a+)+\1$");

        var candidate = new ReleaseCandidate
        {
            Title = new string('a', 40) + "!",
            Guid = "guid-hazard",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("https://example.invalid/r"),
        };

        var stopwatch = Stopwatch.StartNew();
        var verdict = rule.Evaluate(candidate);
        stopwatch.Stop();

        Assert.Equal(Verdict.Unknown, verdict);
        // Must resolve close to MatchTimeout, not hang indefinitely (naive backtracking on this
        // input/pattern pair can take longer than the age of the universe without a timeout).
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Evaluate took {stopwatch.Elapsed}, expected bounded by MatchTimeout.");
    }

    [Fact]
    public void Evaluate_BenignPattern_StillMatchesNormally()
    {
        var rule = new FilterRule("benign", isAllow: false, Precedence.Normal, "CAM");
        var candidate = new ReleaseCandidate
        {
            Title = "Movie.2024.CAM.x264",
            Guid = "guid-benign",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("https://example.invalid/r"),
        };

        Assert.Equal(Verdict.Reject, rule.Evaluate(candidate));
    }
}
