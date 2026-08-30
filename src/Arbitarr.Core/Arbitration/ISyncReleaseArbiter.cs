using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Arbitration;

/// <summary>
/// AC14b: the synchronous, human-opt-in AI arbitration path for the ad-hoc admin search endpoint.
///
/// <para>
/// This interface exists so <c>Arbitarr.Api</c> can offer the opt-in without ever referencing
/// <c>Arbitarr.Ai</c> (AC6a) — the implementation lives in <c>Arbitarr.Ai</c> on top of the
/// existing <c>ReleaseClassifier</c>/<c>IOllamaClient</c> plumbing, and is registered only in
/// <c>Arbitarr.Host/Program.cs</c> (the sole DI composition root).
/// </para>
///
/// <para>
/// This is strictly separate from the Q1-B machine (Torznab/Newznab) path, which remains
/// cached-verdicts-only and never calls this interface or any other inline classification. This
/// abstraction is reachable only from the human ad-hoc search endpoint, behind an explicit
/// request opt-in flag and an admin-gated route.
/// </para>
///
/// <para>
/// P1 fail-open: any Ollama error, timeout, or budget overrun must surface as
/// <see cref="Verdict.Unknown"/> for the affected candidate(s) — never an exception, and never a
/// suppressed/omitted candidate.
/// </para>
/// </summary>
public interface ISyncReleaseArbiter
{
    /// <summary>
    /// Arbitrates each candidate in <paramref name="candidates"/>, returning exactly one
    /// <see cref="ArbitrationOutcome"/> per input candidate, in the same order. Never rewrites
    /// <see cref="ReleaseCandidate.Size"/>, <see cref="ReleaseCandidate.Category"/>, or
    /// <see cref="ReleaseCandidate.Guid"/>.
    /// </summary>
    Task<IReadOnlyList<ArbitrationOutcome>> ArbitrateAsync(
        IReadOnlyList<ReleaseCandidate> candidates,
        ArbitrationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// AC14b outcome verdict, distinct from the raw Ollama-layer verdict string: unlike
/// <c>OllamaVerdict</c> (accept/reject only), this can express <see cref="Unknown"/> for the P1
/// fail-open case (error, timeout, or budget overrun).
/// </summary>
public enum Verdict
{
    /// <summary>AI classification was not obtained (error, timeout, budget overrun, or circuit open) — fail-open.</summary>
    Unknown,

    /// <summary>AI classifier accepted the candidate.</summary>
    Accept,

    /// <summary>AI classifier rejected the candidate.</summary>
    Reject,
}

/// <summary>One candidate's arbitration result. <see cref="Confidence"/> is null whenever <see cref="Verdict"/> is <see cref="Verdict.Unknown"/>.</summary>
public sealed record ArbitrationOutcome(string Guid, Verdict Verdict, double? Confidence);

/// <summary>
/// Per-call context for <see cref="ISyncReleaseArbiter.ArbitrateAsync"/>. <see cref="Budget"/> is
/// the AC14b human-latency budget (distinct from the AC14 machine budget) — the implementation
/// enforces it via a <see cref="CancellationTokenSource"/> timeout linked to the caller's own
/// cancellation token, and treats a budget overrun as fail-open (<see cref="Verdict.Unknown"/>),
/// never as an exception surfaced to the caller.
/// </summary>
public sealed record ArbitrationContext(TimeSpan Budget);
