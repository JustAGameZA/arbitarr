using Arbitarr.Core.Releases;

namespace Arbitarr.Ai;

/// <summary>
/// Classifies a single release via a local Ollama model. Abstracted behind an interface so tests
/// can supply a fake that fails when invoked on the request path (M5-4: only cached verdicts may
/// apply inline — a real/fake client call must only ever happen from the background classifier
/// worker, never from a search request).
/// </summary>
public interface IOllamaClient
{
    /// <summary>
    /// Classifies <paramref name="candidate"/>, returning the model's verdict and confidence.
    /// Callers are expected to be background/off-request-path code only (Q1-B).
    /// </summary>
    Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default);
}

/// <summary>Result of a single Ollama classification call.</summary>
/// <param name="Verdict">"accept" or "reject", as constrained by <see cref="VerdictSchema"/>.</param>
/// <param name="Confidence">Model-reported confidence in [0,1].</param>
public sealed record OllamaVerdict(string Verdict, double Confidence);
