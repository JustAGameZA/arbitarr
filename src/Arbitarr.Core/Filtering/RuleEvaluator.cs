using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Evaluates a single <see cref="ReleaseCandidate"/> against a <see cref="FilterProfile"/>'s
/// rules (Step 5). Semantics: deny rules take precedence over allow rules at the same
/// <see cref="Precedence"/> tier (a hazard, once flagged, is never silently overridden by a
/// same-tier allow — the safer outcome wins on a tie); across tiers, the highest
/// <see cref="Precedence"/> value that produced a non-<see cref="Verdict.Unknown"/> result wins.
/// A candidate matching no rule is <see cref="Verdict.Accept"/> (default-allow: absence of a rule
/// is not itself a suppression reason).
/// </summary>
public static class RuleEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="candidate"/> against every rule in <paramref name="profile"/>,
    /// returning the winning verdict and the rule that produced it (or null if no rule matched,
    /// in which case the verdict is <see cref="Verdict.Accept"/>). Uses <see cref="TimeProvider.System"/>
    /// to enforce <see cref="FilterProfile.TotalEvaluationBudget"/>; see the
    /// <see cref="Evaluate(FilterProfile, ReleaseCandidate, TimeProvider)"/> overload to inject a
    /// fake clock for tests.
    /// </summary>
    public static (Verdict Verdict, IFilterRule? MatchedRule) Evaluate(FilterProfile profile, ReleaseCandidate candidate)
        => Evaluate(profile, candidate, TimeProvider.System);

    /// <summary>
    /// Evaluates <paramref name="candidate"/> against every rule in <paramref name="profile"/>,
    /// returning the winning verdict and the rule that produced it (or null if no rule matched,
    /// in which case the verdict is <see cref="Verdict.Accept"/>).
    ///
    /// M4 security review (MEDIUM): each rule already bounds itself via
    /// <see cref="FilterRule.MatchTimeout"/>, but a profile with many rules has no whole-request
    /// budget without this check. Once elapsed wall-clock time (measured via
    /// <paramref name="timeProvider"/>) exceeds <see cref="FilterProfile.TotalEvaluationBudget"/>,
    /// evaluation of remaining rules stops immediately and the candidate resolves using whatever
    /// verdict has won so far (fail open, P1: never suppress on a budget cutoff — a candidate no
    /// rule has yet rejected still defaults to <see cref="Verdict.Accept"/>, exactly like the
    /// per-rule ReDoS timeout in <see cref="FilterRule.Evaluate"/>).
    /// </summary>
    public static (Verdict Verdict, IFilterRule? MatchedRule) Evaluate(
        FilterProfile profile,
        ReleaseCandidate candidate,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(timeProvider);

        IFilterRule? winningRule = null;
        var winningVerdict = Verdict.Unknown;
        var startedAt = timeProvider.GetTimestamp();

        foreach (var rule in profile.Rules)
        {
            if (timeProvider.GetElapsedTime(startedAt) >= profile.TotalEvaluationBudget)
            {
                // Aggregate budget exhausted: stop evaluating further rules and fail open with
                // whatever verdict has won so far (Accept/no-rule-matched if none has).
                break;
            }

            var verdict = rule.Evaluate(candidate);
            if (verdict == Verdict.Unknown)
            {
                continue;
            }

            if (winningRule is null)
            {
                winningRule = rule;
                winningVerdict = verdict;
                continue;
            }

            if (rule.Precedence > winningRule.Precedence)
            {
                winningRule = rule;
                winningVerdict = verdict;
            }
            else if (rule.Precedence == winningRule.Precedence && verdict == Verdict.Reject)
            {
                // Tie at the same precedence tier: deny wins over allow (safer outcome).
                winningRule = rule;
                winningVerdict = verdict;
            }
        }

        return winningRule is null
            ? (Verdict.Accept, null)
            : (winningVerdict, winningRule);
    }
}
