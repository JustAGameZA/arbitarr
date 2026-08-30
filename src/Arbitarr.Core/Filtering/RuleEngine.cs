using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Applies a <see cref="FilterProfile"/>'s rules across a batch of release candidates (Step 5),
/// producing the surviving candidates plus a <see cref="SuppressionRecord"/> for each one
/// <see cref="RuleEvaluator"/> rejected. This is the deterministic rule-engine slot of the
/// suppression precedence chain (see <see cref="SuppressionPrecedenceChain"/>) — it does not know
/// about shadow mode; callers wrap its output with <see cref="ShadowModeGate"/> when shadow mode
/// applies.
/// </summary>
public sealed class RuleEngine
{
    private readonly FilterProfile _profile;

    public RuleEngine(FilterProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>The profile this engine evaluates candidates against.</summary>
    public FilterProfile Profile => _profile;

    /// <summary>
    /// Evaluates every candidate in <paramref name="candidates"/> against <see cref="Profile"/>.
    /// Returns the candidates that were not rejected (accepted, including no-rule-matched) and one
    /// <see cref="SuppressionRecord"/> per rejected candidate, in input order.
    /// </summary>
    public RuleEngineResult Evaluate(
        IReadOnlyList<ReleaseCandidate> candidates,
        string queryKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var survivors = new List<ReleaseCandidate>(candidates.Count);
        var suppressions = new List<SuppressionRecord>();

        foreach (var candidate in candidates)
        {
            var (verdict, matchedRule) = RuleEvaluator.Evaluate(_profile, candidate);
            if (verdict == Verdict.Reject)
            {
                var identity = new ReleaseIdentity(_profile.Name, candidate.Guid);
                var reason = matchedRule is not null
                    ? $"Denied by rule '{matchedRule.Name}' (profile '{_profile.Name}', query '{queryKey}')."
                    : $"Denied (profile '{_profile.Name}', query '{queryKey}').";
                suppressions.Add(new SuppressionRecord(identity, reason, now));
                continue;
            }

            survivors.Add(candidate);
        }

        return new RuleEngineResult(survivors, suppressions);
    }
}

/// <summary>Result of a <see cref="RuleEngine.Evaluate"/> pass over a batch of candidates.</summary>
/// <param name="Survivors">Candidates not rejected by the profile's rules.</param>
/// <param name="Suppressions">One record per candidate that was rejected.</param>
public sealed record RuleEngineResult(
    IReadOnlyList<ReleaseCandidate> Survivors,
    IReadOnlyList<SuppressionRecord> Suppressions);
