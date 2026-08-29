using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Contract for a single filter rule evaluated against a release candidate.
/// Behavior (rule evaluation) is implemented in later steps; this is the contract only.
/// </summary>
public interface IFilterRule
{
    /// <summary>Human-readable rule name, surfaced in suppression audit records.</summary>
    string Name { get; }

    /// <summary>Relative precedence of this rule when multiple rules apply.</summary>
    Precedence Precedence { get; }

    /// <summary>
    /// Whether this is an allow rule (true) or a deny rule (false). Consulted by
    /// <see cref="SuppressionPrecedenceChain"/> to place a matching rule in the allow-rule or
    /// deny-rule slot of the fixed D3 chain, independent of <see cref="Evaluate"/>'s own verdict.
    /// </summary>
    bool IsAllow { get; }

    /// <summary>Evaluates this rule against a release candidate.</summary>
    Verdict Evaluate(ReleaseCandidate candidate);
}
