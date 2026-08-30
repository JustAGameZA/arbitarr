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
    private readonly string _sourceName;

    public ClassifierWorker(
        ReleaseClassifier classifier,
        IVerdictCacheWriter cacheWriter,
        AiModelIdentity modelIdentity,
        string sourceName)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _cacheWriter = cacheWriter ?? throw new ArgumentNullException(nameof(cacheWriter));
        _modelIdentity = modelIdentity ?? throw new ArgumentNullException(nameof(modelIdentity));
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
    }

    /// <summary>
    /// Classifies <paramref name="candidate"/> and writes the result to the verdict cache. A
    /// failed/fail-open classification (see <see cref="ReleaseClassifier.TryClassifyAsync"/>)
    /// writes nothing — the cache simply stays a miss for this release until a later cycle
    /// succeeds, never poisoning the cache with a placeholder/guessed verdict.
    /// </summary>
    public async Task ClassifyAndCacheAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var result = await _classifier.TryClassifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        var key = VerdictCacheKey.Compute(
            candidate, _sourceName, _modelIdentity.ModelName, _modelIdentity.ModelDigest, _modelIdentity.PromptVersion);

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
