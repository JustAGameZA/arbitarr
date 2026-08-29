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
    /// in which case the verdict is <see cref="Verdict.Accept"/>).
    /// </summary>
    public static (Verdict Verdict, IFilterRule? MatchedRule) Evaluate(FilterProfile profile, ReleaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);

        IFilterRule? winningRule = null;
        var winningVerdict = Verdict.Unknown;

        foreach (var rule in profile.Rules)
        {
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
