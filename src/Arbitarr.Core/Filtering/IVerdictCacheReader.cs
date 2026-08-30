namespace Arbitarr.Core.Filtering;

/// <summary>
/// Read-only lookup into the persisted AI verdict cache, consulted by the
/// <see cref="SuppressionPrecedenceChain"/>'s AI slot. Deliberately synchronous and read-only:
/// Q1-B requires that only <em>already-cached</em> verdicts ever apply inline during a search
/// request — an uncached release must pass through unjudged rather than trigger a live model call
/// on the request path (background classification, via a worker, is the only path that populates
/// the cache). This interface exists so <c>Arbitarr.Core.Filtering</c> can depend on the cache
/// shape without referencing <c>Arbitarr.Data</c> directly (AC6), the same pattern used by
/// <c>Arbitarr.Core.Sources.CircuitBreaker.IAsyncCircuitBreaker</c>.
/// </summary>
public interface IVerdictCacheReader
{
    /// <summary>
    /// Returns the cached verdict for <paramref name="releaseKeyHash"/> (as computed by
    /// <see cref="VerdictCacheKey.Compute"/>), or <see langword="null"/> when no cache entry
    /// exists — callers must treat a miss as "pass through unjudged," never as an implicit reject
    /// or an invitation to call the model inline.
    /// </summary>
    CachedVerdict? TryGet(string releaseKeyHash);
}

/// <summary>A cached AI verdict as read by <see cref="IVerdictCacheReader"/>.</summary>
/// <param name="Verdict">The cached verdict (<see cref="Filtering.Verdict.Accept"/> or <see cref="Filtering.Verdict.Reject"/>).</param>
/// <param name="Confidence">Model-reported confidence in [0,1].</param>
/// <param name="RewrittenTitle">
/// Worker-produced, cached title rewrite (M5-8/AC26b, R17), or <see langword="null"/> when no
/// rewrite was produced (normalization disabled at classify time, or the differential-parse guard
/// rejected it). Never computed inline on the render path — only ever consumed here.
/// </param>
public sealed record CachedVerdict(Verdict Verdict, double Confidence, string? RewrittenTitle = null);

/// <summary>
/// Write-side counterpart of <see cref="IVerdictCacheReader"/>, used only by background
/// classification code (e.g. <c>Arbitarr.Ai.ClassifierWorker</c>) to persist a freshly computed
/// verdict. Kept in <c>Arbitarr.Core.Filtering</c> (not <c>Arbitarr.Data</c>) for the same AC6
/// reason as the reader: <c>Arbitarr.Ai</c> may depend on Core only, never on Data directly: Host
/// composes the concrete, persistence-backed implementation and injects it as this interface.
/// </summary>
public interface IVerdictCacheWriter
{
    /// <summary>
    /// Persists (or overwrites) the verdict for <paramref name="releaseKeyHash"/> under the given
    /// model identity. A later change to <paramref name="modelName"/>/<paramref name="modelDigest"/>/
    /// <paramref name="promptVersion"/> naturally produces a different key (R17) rather than
    /// overwriting a prior model's verdict in place.
    /// </summary>
    /// <param name="rewrittenTitle">
    /// Worker-produced title rewrite to cache alongside the verdict (M5-8/AC26b, R17), or
    /// <see langword="null"/> when no rewrite applies. Defaulted so existing callers/fakes that
    /// predate title normalization keep compiling unchanged.
    /// </param>
    Task PutAsync(
        string releaseKeyHash,
        string modelName,
        string modelDigest,
        string promptVersion,
        Verdict verdict,
        double confidence,
        string? rewrittenTitle = null,
        CancellationToken cancellationToken = default);
}
