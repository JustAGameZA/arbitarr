using Arbitarr.Core.Arbitration;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// request-path caller, so reusing it would blur that existing contract.
/// </para>
///
/// <para>
/// Fail-open here deliberately differs from <see cref="ReleaseClassifier.TryClassifyAsync"/> in
/// two ways, and neither is an oversight to be tidied into agreement:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The caught <em>set</em> is narrower. That caller catches broad <c>Exception</c> because nothing
/// may escape a background loop; this one names the four transport/circuit/cancellation types it
/// can actually answer for, so a genuine programming error — an <c>InvalidOperationException</c>,
/// say — still propagates out of a request rather than being silently returned as "no verdict".
/// </description></item>
/// <item><description>
/// The <em>logging</em> differs. That caller logs one Warning per failure, because in a background
/// loop every failure is an anomaly. This one is the request path with a per-call budget, where a
/// budget overrun is a routine outcome and a single search carries many candidates, so a Warning
/// apiece would be a log flood rather than a signal — see the level split below.
/// </description></item>
/// </list>
///
/// <para>
/// P1 fail-open: any <em>caught</em> exception from <see cref="IOllamaClient.ClassifyAsync"/> (the
/// set named above), or exceeding the per-call <see cref="ArbitrationContext.Budget"/>, yields
/// <see cref="Verdict.Unknown"/> for that candidate — never surfaced to the caller, never a
/// suppressed candidate.
/// </para>
/// </summary>
public sealed class SyncReleaseArbiter : ISyncReleaseArbiter
{
    private readonly IOllamaClient _ollamaClient;
    private readonly ILogger<SyncReleaseArbiter> _logger;

    public SyncReleaseArbiter(IOllamaClient ollamaClient, ILogger<SyncReleaseArbiter>? logger = null)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _logger = logger ?? NullLogger<SyncReleaseArbiter>.Instance;
    }

    public async Task<IReadOnlyList<ArbitrationOutcome>> ArbitrateAsync(
        IReadOnlyList<ReleaseCandidate> candidates,
        ArbitrationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);

        var outcomes = new List<ArbitrationOutcome>(candidates.Count);

        // Flood control: the per-candidate failures are counted here and reported as ONE Warning for
        // the whole arbitration, rather than one Warning per candidate. A search arbitrates many
        // candidates at once, so the per-candidate shape would write a Warning per candidate per
        // search on the request path. An operator needs to know that this search degraded and what
        // broke, which a single line with counts by exception type answers; the per-candidate detail
        // is still emitted, at Debug.
        var failuresByType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            outcomes.Add(await ArbitrateOneAsync(candidate, context.Budget, failuresByType, cancellationToken).ConfigureAwait(false));
        }

        WarnOnFailures(failuresByType, candidates.Count);

        return outcomes;
    }

    /// <summary>
    /// Writes the single per-arbitration Warning, if any non-cancellation failure occurred. Budget
    /// overruns and caller cancellations are deliberately not counted here: they are routine on this
    /// path (see <see cref="ArbitrateOneAsync"/>), so a Warning for them would be noise rather than
    /// news. They are still visible at Debug, per candidate.
    /// </summary>
    private void WarnOnFailures(Dictionary<string, int> failuresByType, int candidateCount)
    {
        if (failuresByType.Count == 0) return;

        var failed = failuresByType.Values.Sum();
        var breakdown = string.Join(", ", failuresByType.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));

        _logger.LogWarning(
            "AI arbitration failed for {Failed} of {Candidates} candidate(s) in this search, by exception type: {FailuresByType}. " +
            "Arbitration fails open, so those candidates are returned as Unknown and no result is suppressed.",
            failed, candidateCount, breakdown);
    }

    private async Task<ArbitrationOutcome> ArbitrateOneAsync(
        ReleaseCandidate candidate,
        TimeSpan budget,
        Dictionary<string, int> failuresByType,
        CancellationToken cancellationToken)
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
            //
            // That indistinguishability is also why BOTH cancellation arms log at Debug and neither
            // is counted for the Warning: this layer cannot tell a budget overrun from the caller
            // walking away, and neither is an operator-actionable fault. A circuit-open or transport
            // failure is, so it is counted and surfaces in the one summary Warning above.
            var isCancellation = ex is TaskCanceledException or OperationCanceledException;
            if (isCancellation)
            {
                _logger.LogDebug(
                    ex,
                    "AI arbitration for one candidate ended without a verdict ({ExceptionType}): {ExceptionMessage}. " +
                    "This is the per-call budget or the caller's own cancellation, which are indistinguishable here; " +
                    "the candidate fails open to Unknown.",
                    ex.GetType().Name,
                    ex.Message);
            }
            else
            {
                failuresByType[ex.GetType().Name] = failuresByType.GetValueOrDefault(ex.GetType().Name) + 1;

                _logger.LogDebug(
                    ex,
                    "AI arbitration failed for one candidate ({ExceptionType}): {ExceptionMessage}. " +
                    "The candidate fails open to Unknown; the search reports one summary Warning for all such failures.",
                    ex.GetType().Name,
                    ex.Message);
            }

            return new ArbitrationOutcome(candidate.Guid, Verdict.Unknown, Confidence: null);
        }
    }
}
