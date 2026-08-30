using Arbitarr.Core.Arbitration;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Ai;

/// <summary>
/// AC14b implementation of <see cref="ISyncReleaseArbiter"/>: the human ad-hoc-search-only
/// synchronous AI opt-in. Registered only by <c>Arbitarr.Host/Program.cs</c> (AC6 sole composition
/// root) — never reachable from the Q1-B machine (Torznab/Newznab) path.
///
/// <para>
/// Calls <see cref="IOllamaClient"/> directly rather than going through
/// <see cref="ReleaseClassifier"/>: that type's doc contract is deliberately scoped to
/// background/off-request-path callers only (Q1-B, M5-4), and this is a distinct, intentional
/// request-path caller, so reusing it would blur that existing contract. The fail-open exception
/// set mirrors <see cref="ReleaseClassifier.TryClassifyAsync"/> exactly.
/// </para>
///
/// <para>
/// P1 fail-open: any exception from <see cref="IOllamaClient.ClassifyAsync"/>, or exceeding the
/// per-call <see cref="ArbitrationContext.Budget"/>, yields <see cref="Verdict.Unknown"/> for that
/// candidate — never an exception surfaced to the caller, never a suppressed candidate.
/// </para>
/// </summary>
public sealed class SyncReleaseArbiter : ISyncReleaseArbiter
{
    private readonly IOllamaClient _ollamaClient;

    public SyncReleaseArbiter(IOllamaClient ollamaClient)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    public async Task<IReadOnlyList<ArbitrationOutcome>> ArbitrateAsync(
        IReadOnlyList<ReleaseCandidate> candidates,
        ArbitrationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        var outcomes = new List<ArbitrationOutcome>(candidates.Count);
        foreach (var candidate in candidates)
        {
            outcomes.Add(await ArbitrateOneAsync(candidate, context.Budget, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    private async Task<ArbitrationOutcome> ArbitrateOneAsync(
        ReleaseCandidate candidate, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var budgetCts = new CancellationTokenSource(budget);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetCts.Token);

        try
        {
            var verdict = await _ollamaClient.ClassifyAsync(candidate, linkedCts.Token).ConfigureAwait(false);
            return new ArbitrationOutcome(
                candidate.Guid,
                verdict.Verdict.Equals("accept", StringComparison.OrdinalIgnoreCase) ? Verdict.Accept : Verdict.Reject,
                verdict.Confidence);
        }
        catch (Exception ex) when (ex is OllamaCircuitOpenException or HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // P1 fail-open. Covers both an outright Ollama failure and a budget overrun (the linked
            // token's cancellation surfaces as TaskCanceledException/OperationCanceledException) —
            // the caller's own cancellationToken firing is indistinguishable at this layer, and
            // fail-open is the correct response either way (never propagate a hard cancellation
            // that would abort producing a response for the *other* candidates in the batch).
            return new ArbitrationOutcome(candidate.Guid, Verdict.Unknown, Confidence: null);
        }
    }
}
