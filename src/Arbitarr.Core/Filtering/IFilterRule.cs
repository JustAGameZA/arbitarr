using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Contract for a single filter rule evaluated against a release candidate.
/// Behavior (rule evaluation) is implemented in later steps; this is the contract only.
/// </summary>
public interface IFilterRule
{
    /// <summary>Relative precedence of this rule when multiple rules apply.</summary>
    Precedence Precedence { get; }

    /// <summary>Evaluates this rule against a release candidate.</summary>
    Verdict Evaluate(ReleaseCandidate candidate);
}
