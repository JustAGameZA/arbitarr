namespace Arbitarr.Core.Filtering;

/// <summary>
/// A named, ordered collection of <see cref="IFilterRule"/>s (Step 5). Distinct API keys can
/// resolve to distinct profiles (A3); a profile flagged <see cref="IsDefault"/> applies when no
/// API key mapping exists. Purely an in-memory rule set — persistence is owned by
/// <c>Arbitarr.Data.Entities.FilterProfileEntry</c>/<c>FilterRuleEntry</c> outside Core.
/// </summary>
public sealed class FilterProfile
{
    /// <summary>
    /// Default upper bound on the total wall-clock time <see cref="RuleEvaluator.Evaluate"/> may
    /// spend evaluating one candidate against this profile's rules (M4 security review, MEDIUM:
    /// unbounded aggregate evaluation time). Each individual rule already bounds itself via
    /// <see cref="FilterRule.MatchTimeout"/> (250ms), but a profile with many rules has no
    /// whole-request budget without this — 2s is generous headroom above a handful of individual
    /// timeouts while still bounding the worst case (many hazardous patterns in one profile).
    /// When exceeded, evaluation stops and fails open (P1) exactly like a single rule's timeout.
    /// </summary>
    public static readonly TimeSpan DefaultTotalEvaluationBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Constructs a profile from a name and its rules. Rules are copied into a fixed-order list;
    /// mutating the caller's original collection after construction has no effect on this profile.
    /// </summary>
    public FilterProfile(
        string name,
        IEnumerable<IFilterRule> rules,
        bool isDefault = false,
        TimeSpan? totalEvaluationBudget = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name must not be blank.", nameof(name));
        }

        Name = name;
        IsDefault = isDefault;
        Rules = rules.ToList();
        TotalEvaluationBudget = totalEvaluationBudget ?? DefaultTotalEvaluationBudget;
    }

    /// <summary>Unique, human-readable profile name.</summary>
    public string Name { get; }

    /// <summary>Whether this is the fallback profile used when no API key mapping matches.</summary>
    public bool IsDefault { get; }

    /// <summary>The rules belonging to this profile, in the order they were supplied.</summary>
    public IReadOnlyList<IFilterRule> Rules { get; }

    /// <summary>
    /// Total wall-clock budget for evaluating one candidate against <see cref="Rules"/> (see
    /// <see cref="DefaultTotalEvaluationBudget"/>). Defaults to <see cref="DefaultTotalEvaluationBudget"/>
    /// when not supplied at construction.
    /// </summary>
    public TimeSpan TotalEvaluationBudget { get; }
}
