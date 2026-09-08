using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Core.Tests.Filtering;

/// <summary>
/// arb-nk2 — <see cref="RuleEvaluator.Evaluate(FilterProfile, ReleaseCandidate, TimeProvider)"/>
/// used to check <see cref="FilterProfile.TotalEvaluationBudget"/> at the TOP of the loop, including
/// before the first rule ran. Because <c>Evaluate</c> captures its own start timestamp on entry, the
/// case that actually bit is degenerate rather than a genuine race: a budget of
/// <see cref="TimeSpan.Zero"/> (or a stall so severe that essentially none of the budget remains by
/// the time the loop is entered) made even the FIRST check — elapsed time is always
/// <see cref="TimeSpan.Zero"/> at the very start, and <c>&gt;= TimeSpan.Zero</c> is unconditionally
/// true — break out before a single rule was consulted, silently failing open to
/// <see cref="Verdict.Accept"/> even when the very first rule was a deny. The fix guarantees at least
/// one rule — and specifically the first one, since deny rules are commonly ordered first — always
/// runs, regardless of how small or already-exhausted the budget is; the guard against a genuinely
/// SLOW rule exhausting the budget mid-loop (the case the aggregate budget exists to bound) is
/// unchanged and still stops evaluation at the next iteration boundary.
/// </summary>
public sealed class RuleEvaluatorTests
{
    /// <summary>
    /// THE FIX, PINNED DIRECTLY. <c>RuleEvaluator.Evaluate</c> captures its own <c>startedAt</c>
    /// timestamp on entry, so a stall BEFORE this method is called (a GC pause, a slow caller) cannot
    /// be reproduced by advancing a <see cref="FakeTimeProvider"/> before calling it — the elapsed
    /// time from a freshly-captured start is always <see cref="TimeSpan.Zero"/> regardless of what
    /// the wall clock read a moment earlier. The scenario this guards against is the DEGENERATE case
    /// of that same stall: a budget of <see cref="TimeSpan.Zero"/> (or one already elapsed by the time
    /// the FIRST check runs, which is operationally indistinguishable from zero on a stalled process).
    /// Before the fix, checking the budget at the top of EVERY iteration meant even this first check
    /// — <c>GetElapsedTime(startedAt) &gt;= TimeSpan.Zero</c>, true unconditionally — broke out before
    /// the loop's first (deny) rule was ever consulted, silently failing open to
    /// <see cref="Verdict.Accept"/>. The fix guarantees the first rule always runs regardless of how
    /// small or already-exhausted the budget is.
    /// </summary>
    [Fact]
    public void Deny_rule_still_applies_when_the_aggregate_budget_is_already_exhausted_at_time_zero()
    {
        var timeProvider = new FakeTimeProvider();

        var denyRule = new RecordingRule("deny-first", Precedence.Normal, isAllow: false, Verdict.Reject);
        var profile = new FilterProfile(
            "already-exhausted",
            new IFilterRule[] { denyRule },
            totalEvaluationBudget: TimeSpan.Zero);

        var (verdict, matchedRule) = RuleEvaluator.Evaluate(profile, MakeCandidate(), timeProvider);

        // POSITIVE CONTROL: the rule was actually reached and asked to evaluate -- not skipped by the
        // budget guard before its Evaluate() ever ran. Without this, "the verdict is Reject" could
        // pass for an unrelated reason and would not prove the first rule was reached.
        Assert.Equal(1, denyRule.EvaluateCallCount);
        Assert.Equal(Verdict.Reject, verdict);
        Assert.Same(denyRule, matchedRule);
    }

    /// <summary>
    /// The budget guard must still function BETWEEN iterations: with the SAME already-exhausted
    /// (zero) budget as above, a first rule that does not match still lets evaluation stop before a
    /// SECOND rule is reached, rather than the fix accidentally disabling the guard altogether for the
    /// rest of the profile.
    /// </summary>
    [Fact]
    public void Second_rule_is_still_skipped_when_the_aggregate_budget_is_already_exhausted_at_time_zero()
    {
        var timeProvider = new FakeTimeProvider();

        var firstRule = new RecordingRule("first-no-match", Precedence.Normal, isAllow: true, Verdict.Unknown);
        var secondRule = new RecordingRule("second-deny", Precedence.Normal, isAllow: false, Verdict.Reject);
        var profile = new FilterProfile(
            "already-exhausted",
            new IFilterRule[] { firstRule, secondRule },
            totalEvaluationBudget: TimeSpan.Zero);

        var (verdict, matchedRule) = RuleEvaluator.Evaluate(profile, MakeCandidate(), timeProvider);

        // The first rule was reached (arb-nk2's fix)...
        Assert.Equal(1, firstRule.EvaluateCallCount);
        // ...but the budget guard between iterations still stops the SECOND rule from ever running,
        // and the candidate fails open to Accept exactly as the aggregate-budget contract promises
        // for rules this evaluation did not have time to reach.
        Assert.Equal(0, secondRule.EvaluateCallCount);
        Assert.Equal(Verdict.Accept, verdict);
        Assert.Null(matchedRule);
    }

    /// <summary>
    /// The realistic version of the same bug: a genuinely SLOW first rule (simulated by having its
    /// <c>Evaluate</c> callback advance the fake clock past budget as a side effect, standing in for
    /// wall-clock time actually elapsing while that rule ran) must not prevent a SECOND rule from at
    /// least being reached if it is the one that matters — wait, no: once budget is exhausted mid-loop
    /// the contract is explicitly to stop at the NEXT iteration boundary. This test pins that the
    /// between-iterations guard (unchanged by the fix) still fires correctly when the elapsed time
    /// genuinely grows during evaluation, distinguishing "stalled before the loop starts" (arb-nk2,
    /// fixed: first rule always runs) from "slow during the loop" (unchanged: still bounded).
    /// </summary>
    [Fact]
    public void A_slow_first_rule_that_exhausts_budget_during_evaluation_still_stops_the_second_rule()
    {
        var timeProvider = new FakeTimeProvider();
        var budget = TimeSpan.FromSeconds(2);

        var slowFirstRule = new RecordingRule(
            "slow-first",
            Precedence.Normal,
            isAllow: true,
            Verdict.Unknown,
            onEvaluate: () => timeProvider.Advance(budget + TimeSpan.FromSeconds(1)));
        var secondRule = new RecordingRule("second-deny", Precedence.Normal, isAllow: false, Verdict.Reject);
        var profile = new FilterProfile(
            "slow-rule",
            new IFilterRule[] { slowFirstRule, secondRule },
            totalEvaluationBudget: budget);

        var (verdict, matchedRule) = RuleEvaluator.Evaluate(profile, MakeCandidate(), timeProvider);

        Assert.Equal(1, slowFirstRule.EvaluateCallCount);
        Assert.Equal(0, secondRule.EvaluateCallCount);
        Assert.Equal(Verdict.Accept, verdict);
        Assert.Null(matchedRule);
    }

    /// <summary>
    /// Baseline: with no stall at all, every rule in a small profile runs and precedence/deny-wins-tie
    /// semantics are unaffected by moving the guard — this is not a behaviour change for the
    /// non-degenerate case the aggregate budget exists to bound.
    /// </summary>
    [Fact]
    public void All_rules_still_run_when_the_clock_never_approaches_budget()
    {
        var timeProvider = new FakeTimeProvider();
        var allowRule = new RecordingRule("allow", Precedence.Normal, isAllow: true, Verdict.Accept);
        var denyRule = new RecordingRule("deny", Precedence.Normal, isAllow: false, Verdict.Reject);
        var profile = new FilterProfile(
            "healthy",
            new IFilterRule[] { allowRule, denyRule },
            totalEvaluationBudget: TimeSpan.FromSeconds(2));

        var (verdict, matchedRule) = RuleEvaluator.Evaluate(profile, MakeCandidate(), timeProvider);

        Assert.Equal(1, allowRule.EvaluateCallCount);
        Assert.Equal(1, denyRule.EvaluateCallCount);
        // Same-tier tie: deny wins over allow.
        Assert.Equal(Verdict.Reject, verdict);
        Assert.Same(denyRule, matchedRule);
    }

    private static ReleaseCandidate MakeCandidate() => new()
    {
        Title = "Example.Release.1080p",
        Guid = "guid-1",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("http://192.0.2.10/release/1"),
    };

    private sealed class RecordingRule(
        string name,
        Precedence precedence,
        bool isAllow,
        Verdict result,
        Action? onEvaluate = null) : IFilterRule
    {
        public int EvaluateCallCount { get; private set; }

        public string Name { get; } = name;

        public Precedence Precedence { get; } = precedence;

        public bool IsAllow { get; } = isAllow;

        public Verdict Evaluate(ReleaseCandidate candidate)
        {
            EvaluateCallCount++;
            onEvaluate?.Invoke();
            return result;
        }
    }
}
