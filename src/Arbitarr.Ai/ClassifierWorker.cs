using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Ai;

/// <summary>
/// The one and only place in the codebase that is allowed to call <see cref="IOllamaClient"/>
/// (via <see cref="ReleaseClassifier"/>): a background, off-request-path worker that classifies
/// releases and writes results into the verdict cache (Q1-B). The <see cref="SuppressionPrecedenceChain"/>'s
/// AI slot only ever reads that cache (<see cref="IVerdictCacheReader"/>) — an uncached release
/// simply passes through unjudged on the request path; this worker is what eventually populates
/// the cache asynchronously, out of band from any search.
/// </summary>
public sealed class ClassifierWorker
{
    private readonly ReleaseClassifier _classifier;
    private readonly IVerdictCacheWriter _cacheWriter;
    private readonly AiModelIdentity _modelIdentity;
    private readonly ObservabilityCounters? _counters;

    public ClassifierWorker(
        ReleaseClassifier classifier,
        IVerdictCacheWriter cacheWriter,
        AiModelIdentity modelIdentity,
        ObservabilityCounters? counters = null)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _cacheWriter = cacheWriter ?? throw new ArgumentNullException(nameof(cacheWriter));
        _modelIdentity = modelIdentity ?? throw new ArgumentNullException(nameof(modelIdentity));
        _counters = counters;
    }

    /// <summary>
    /// Classifies <paramref name="candidate"/> and writes the result to the verdict cache. A
    /// failed/fail-open classification (see <see cref="ReleaseClassifier.TryClassifyAsync"/>)
    /// writes nothing — the cache simply stays a miss for this release until a later cycle
    /// succeeds, never poisoning the cache with a placeholder/guessed verdict. The verdict is keyed
    /// under <paramref name="sourceName"/> — the upstream source the release was rendered from — so
    /// it matches the key the render path computes from <c>RenderedRelease.SourceName</c>.
    ///
    /// <para>
    /// arb-s4lg: <paramref name="onFailure"/> and <paramref name="detailAtWarning"/> are forwarded
    /// straight to <see cref="ReleaseClassifier.TryClassifyAsync"/> and are otherwise none of this
    /// type's business. They are per-CALL rather than constructor state on purpose: the caller that
    /// owns them is the Host's polling worker, whose cap resets every cycle, while this type is
    /// resolved from a DI scope before the cycle's loop begins — constructor state would have to
    /// close over a counter that does not exist yet.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">
    /// ALWAYS pass this — positionally or by name — because the optional parameters after it mean a
    /// caller naming only <paramref name="onFailure"/> or <paramref name="detailAtWarning"/> silently
    /// drops the token, and this is forwarded straight to
    /// <see cref="ReleaseClassifier.TryClassifyAsync"/> whose
    /// <c>when (!cancellationToken.IsCancellationRequested)</c> catch filter is what separates
    /// propagating a shutdown from failing open as "no verdict".
    /// </param>
    /// <param name="onFailure">Optional; receives the exception type name of a failed call.</param>
    /// <param name="detailAtWarning">Whether that call's detail line is a Warning or a Debug row.</param>
    public async Task ClassifyAndCacheAsync(
        ReleaseCandidate candidate,
        string sourceName,
        CancellationToken cancellationToken = default,
        Action<string>? onFailure = null,
        bool detailAtWarning = true)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sourceName);

        var result = await _classifier
            .TryClassifyAsync(candidate, cancellationToken, onFailure, detailAtWarning)
            .ConfigureAwait(false);
        _counters?.RecordLlmCall(failed: result is null);
        if (result is null)
        {
            return;
        }

        var key = VerdictCacheKey.Compute(
            candidate,
            sourceName,
            _modelIdentity.ModelName,
            _modelIdentity.ModelDigest,
            _modelIdentity.PromptVersion,
            _modelIdentity.DecodingIdentity);

        var verdict = string.Equals(result.Verdict, "reject", StringComparison.OrdinalIgnoreCase)
            ? Verdict.Reject
            : Verdict.Accept;

        await _cacheWriter.PutAsync(
            key,
            _modelIdentity.ModelName,
            _modelIdentity.ModelDigest,
            _modelIdentity.PromptVersion,
            verdict,
            result.Confidence,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
