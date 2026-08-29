using ArrSearcher.Core.Releases;

namespace ArrSearcher.Core.Pipeline;

/// <summary>
/// Contract for a single stage in the search-result processing pipeline
/// (Merge, Identity, Match, Dedup, Filter). Contracts only in Step 1 — implementations
/// land in later steps alongside their respective owning projects.
/// </summary>
public interface IPipelineStage
{
    /// <summary>A short, stable name identifying this pipeline stage.</summary>
    string Name { get; }

    /// <summary>Processes the given release candidates, producing the stage's output set.</summary>
    Task<IReadOnlyList<ReleaseCandidate>> ProcessAsync(
        IReadOnlyList<ReleaseCandidate> candidates,
        CancellationToken cancellationToken = default);
}
