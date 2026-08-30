using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;

namespace Arbitarr.Ai;

/// <summary>
/// Background-only classification entry point (Q1-B): calls <see cref="IOllamaClient"/> to obtain
/// a fresh verdict, protected by the same fail-open contract every other adapter uses via
/// <see cref="IAsyncCircuitBreaker"/> — reused, not reimplemented (M5 step 5). Never called from
/// the search request path; only <see cref="ClassifierWorker"/> and offline/background code should
/// hold a reference to this type. The suppression chain's AI slot never calls this directly — it
/// only reads <see cref="IVerdictCacheReader"/>, which this type's caller is responsible for
/// populating after a successful classification.
///
/// Fail-open (M5-3): when the circuit breaker is open, or the call otherwise fails, this returns
/// <see langword="null"/> rather than throwing out of a background loop — callers treat a null
/// result identically to "no verdict yet" (deterministic-only filtering continues to apply; the
/// cache is simply not populated for that release this cycle).
/// </summary>
public sealed class ReleaseClassifier
{
    private readonly IOllamaClient _ollamaClient;

    public ReleaseClassifier(IOllamaClient ollamaClient)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    /// <summary>
    /// Attempts to classify <paramref name="candidate"/>. Returns <see langword="null"/> on any
    /// failure (circuit open, timeout, malformed response) — deterministic-only behavior is always
    /// the safe fallback (M5-3), never an unhandled exception surfacing out of the background
    /// worker's loop.
    /// </summary>
    public async Task<OllamaVerdict?> TryClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            return await _ollamaClient.ClassifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OllamaCircuitOpenException or HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }
}
