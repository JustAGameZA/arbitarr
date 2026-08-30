using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// The fixed suppression-source precedence chain (D3): <b>allow-rule &gt; deny-rule &gt; AI verdict
/// &gt; pass</b>, implemented as a single ordered evaluation in one place so no individual source
/// can reorder it. Distinct from <see cref="Precedence"/> (a rule-priority enum ordering rules
/// *within* the deterministic rule engine) — that type is intentionally left untouched (A5); this
/// chain orders entire *suppression sources*, not rules within one source.
///
/// The deterministic rule-engine slot (allow-rule and deny-rule) delegates to
/// <see cref="RuleEvaluator"/> — the same precedence-tie semantics (deny wins a same-tier tie) used
/// by <see cref="RuleEngine"/> — rather than re-scanning <see cref="FilterProfile.Rules"/>
/// independently, so there is exactly one implementation of "which rule wins" in the codebase.
///
/// The AI slot is a no-op stub at M4 (always <see cref="Verdict.Unknown"/>, i.e. abstains) — M5
/// fills it with the real classifier. Testing the chain's shape now (with the stub) is what
/// prevents M5 from quietly reordering it later.
///
/// Every suppression the chain produces is returned as a <see cref="SuppressionRecord"/> (via
/// <see cref="EvaluateBatch"/>), attributed to the winning <see cref="SuppressionSource"/> and rule
/// name, so a caller can persist it to <c>Arbitarr.Data.Entities.SuppressionAuditLogEntry</c>
/// (owned outside Core per AC6) with zero suppressions going unrecorded (M4-5).
///
/// Shadow mode is applied once, at the chain level (AC12/D3), not per-source: whichever source
/// wins the chain, the final enforce/record-only decision is the same single toggle
/// (<c>Arbitarr.Core.Settings.SettingKey.ShadowMode</c>). Callers pass the already-resolved boolean
/// value; this type has no dependency on the persistence layer (AC6).
/// </summary>
public static class SuppressionPrecedenceChain
{
    /// <summary>
    /// Evaluates <paramref name="candidate"/> through the fixed chain: an allow-rule match wins
    /// outright (even over a deny-rule or AI match), a deny-rule match wins over an AI match, and
    /// an AI match (once M5 supplies one) wins over no match at all ("pass"). Returns the winning
    /// verdict together with which source produced it, for attribution in the audit trail (P3).
    ///
    /// The AI slot is cache-only (Q1-B): <paramref name="verdictCacheReader"/> is consulted for an
    /// already-computed verdict; a miss passes through unjudged rather than triggering a live model
    /// call on the request path. Passing <see langword="null"/> for <paramref name="verdictCacheReader"/>
    /// (the default) keeps the AI slot an abstain-only no-op, e.g. for pre-M5 callers/tests.
    /// </summary>
    public static ChainResult Evaluate(
        FilterProfile profile,
        ReleaseCandidate candidate,
        double aiConfidenceThreshold,
        IVerdictCacheReader? verdictCacheReader = null,
        string sourceName = "",
        AiModelIdentity? modelIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidate);

        var (ruleVerdict, matchedRule) = RuleEvaluator.Evaluate(profile, candidate);
        if (ruleVerdict == Verdict.Accept && matchedRule is not null)
        {
            return new ChainResult(Verdict.Accept, SuppressionSource.AllowRule, matchedRule.Name);
        }

        if (ruleVerdict == Verdict.Reject)
        {
            return new ChainResult(Verdict.Reject, SuppressionSource.DenyRule, matchedRule?.Name);
        }

        var aiVerdict = EvaluateAi(candidate, aiConfidenceThreshold, verdictCacheReader, sourceName, modelIdentity);
        if (aiVerdict == Verdict.Reject)
        {
            return new ChainResult(Verdict.Reject, SuppressionSource.Ai, null);
        }

        return new ChainResult(Verdict.Accept, SuppressionSource.Pass, null);
    }

    /// <summary>
    /// Evaluates every candidate in <paramref name="candidates"/> through <see cref="Evaluate"/>,
    /// returning the survivors plus one <see cref="SuppressionRecord"/> per rejected candidate
    /// (M4-5: zero suppressions occur without a record). Mirrors <see cref="RuleEngine.Evaluate"/>'s
    /// shape so callers can treat the chain as a drop-in, source-attributed replacement for the
    /// deterministic-only engine once M5 supplies a real AI slot.
    /// </summary>
    public static RuleEngineResult EvaluateBatch(
        FilterProfile profile,
        IReadOnlyList<ReleaseCandidate> candidates,
        double aiConfidenceThreshold,
        string queryKey,
        DateTimeOffset now,
        IVerdictCacheReader? verdictCacheReader = null,
        string sourceName = "",
        AiModelIdentity? modelIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(candidates);

        var survivors = new List<ReleaseCandidate>(candidates.Count);
        var suppressions = new List<SuppressionRecord>();

        foreach (var candidate in candidates)
        {
            var result = Evaluate(profile, candidate, aiConfidenceThreshold, verdictCacheReader, sourceName, modelIdentity);
            if (result.Verdict == Verdict.Reject)
            {
                var identity = new ReleaseIdentity(profile.Name, candidate.Guid);
                var reason = result.RuleName is not null
                    ? $"Suppressed by {result.Source} '{result.RuleName}' (profile '{profile.Name}', query '{queryKey}')."
                    : $"Suppressed by {result.Source} (profile '{profile.Name}', query '{queryKey}').";
                suppressions.Add(new SuppressionRecord(identity, reason, now));
                continue;
            }

            survivors.Add(candidate);
        }

        return new RuleEngineResult(survivors, suppressions);
    }

    /// <summary>
    /// AI slot (M5): cache-only lookup (Q1-B) — never calls the model inline. A miss (no
    /// <paramref name="verdictCacheReader"/>, or no cache entry for this release) abstains
    /// (<see cref="Verdict.Unknown"/>), letting the candidate pass through unjudged exactly like
    /// the M4 no-op stub did. A cache hit only suppresses when the cached verdict is
    /// <see cref="Verdict.Reject"/> AND its confidence meets <paramref name="aiConfidenceThreshold"/>
    /// (D3); a low-confidence reject also abstains rather than suppressing.
    /// </summary>
    private static Verdict EvaluateAi(
        ReleaseCandidate candidate,
        double aiConfidenceThreshold,
        IVerdictCacheReader? verdictCacheReader,
        string sourceName,
        AiModelIdentity? modelIdentity)
    {
        if (verdictCacheReader is null || modelIdentity is null)
        {
            return Verdict.Unknown;
        }

        var key = VerdictCacheKey.Compute(
            candidate, sourceName, modelIdentity.ModelName, modelIdentity.ModelDigest, modelIdentity.PromptVersion);

        var cached = verdictCacheReader.TryGet(key);
        if (cached is null)
        {
            return Verdict.Unknown;
        }

        if (cached.Verdict == Verdict.Reject && cached.Confidence >= aiConfidenceThreshold)
        {
            return Verdict.Reject;
        }

        return Verdict.Unknown;
    }
}

/// <summary>
/// The model/prompt identity used to compute and invalidate AI verdict cache keys (R17: a
/// model-name or prompt-version change invalidates previously cached verdicts, since they key on
/// this identity).
/// </summary>
/// <param name="ModelName">Ollama model name/tag (e.g. <c>qwen2.5:7b-instruct-q4_K_M</c>).</param>
/// <param name="ModelDigest">Model content digest, so a same-named model with different weights invalidates too.</param>
/// <param name="PromptVersion">Version tag of the classification prompt template.</param>
public sealed record AiModelIdentity(string ModelName, string ModelDigest, string PromptVersion);

/// <summary>Which slot of the <see cref="SuppressionPrecedenceChain"/> produced a decision.</summary>
public enum SuppressionSource
{
    /// <summary>An allow rule matched and won outright.</summary>
    AllowRule,

    /// <summary>A deny rule matched (no allow rule matched).</summary>
    DenyRule,

    /// <summary>The AI verdict layer matched (no allow or deny rule matched).</summary>
    Ai,

    /// <summary>No source matched; the candidate passes by default.</summary>
    Pass,
}

/// <summary>
/// Result of evaluating one candidate through the <see cref="SuppressionPrecedenceChain"/>.
/// </summary>
/// <param name="Verdict">The winning verdict (<see cref="Filtering.Verdict.Accept"/> or <see cref="Filtering.Verdict.Reject"/>).</param>
/// <param name="Source">Which chain slot produced <paramref name="Verdict"/>.</param>
/// <param name="RuleName">The specific rule name that matched, when <paramref name="Source"/> is a rule slot; otherwise null.</param>
public sealed record ChainResult(Verdict Verdict, SuppressionSource Source, string? RuleName);
