using Arbitarr.Api.Rendering;
using Arbitarr.Core.Pipeline;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Search;

/// <summary>
/// Merge-stage implementation of <see cref="IMergeStage"/> (Arbitarr.Core.Pipeline): fans a
/// <see cref="SearchQuery"/> out to every configured <see cref="IUpstreamSource"/> in parallel
/// and unions the resulting <see cref="ReleaseCandidate"/> sets, tagging each with its
/// originating source name for later guid computation. This is the only pipeline stage M1
/// implements; Identity/Match/Dedup/Filter remain contract-only until later milestones.
///
/// A source whose search fails with <see cref="RequestLimitReachedException"/> is recorded so
/// the caller can surface a Torznab/Newznab rate-limit element (M1-9) instead of losing the
/// whole merged response; any other per-source failure is treated the same way (that source's
/// results are simply omitted from the union — zero renderer changes, M1-10).
/// </summary>
public sealed class UpstreamMergeStage : IMergeStage
{
    private readonly IReadOnlyList<IUpstreamSource> _sources;

    public UpstreamMergeStage(IReadOnlyList<IUpstreamSource> sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public string Name => "UpstreamMerge";

    /// <summary>
    /// <see cref="IPipelineStage"/> conformance: candidates in, same candidates out unchanged.
    /// The actual fan-out/merge happens in <see cref="MergeAsync"/>, which additionally needs
    /// the originating <see cref="SearchQuery"/> and per-source identity that the base
    /// contract's signature doesn't carry.
    /// </summary>
    public Task<IReadOnlyList<ReleaseCandidate>> ProcessAsync(
        IReadOnlyList<ReleaseCandidate> candidates,
        CancellationToken cancellationToken = default) => Task.FromResult(candidates);

    /// <summary>Fans <paramref name="query"/> out to all sources and returns the merged, source-tagged union.</summary>
    public async Task<MergeResult> MergeAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tasks = _sources.Select(async source =>
        {
            try
            {
                var results = await source.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                return (Source: source, Results: results, RateLimited: false);
            }
            catch (RequestLimitReachedException)
            {
                return (Source: source, Results: (IReadOnlyList<ReleaseCandidate>)Array.Empty<ReleaseCandidate>(), RateLimited: true);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                return (Source: source, Results: (IReadOnlyList<ReleaseCandidate>)Array.Empty<ReleaseCandidate>(), RateLimited: false);
            }
        }).ToArray();

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);

        var merged = completed
            .SelectMany(t => t.Results.Select(r => new RenderedRelease(t.Source.Name, r)))
            .ToArray();

        var rateLimitedSources = completed
            .Where(t => t.RateLimited)
            .Select(t => t.Source.Name)
            .ToArray();

        return new MergeResult(merged, rateLimitedSources);
    }
}

/// <summary>Result of an upstream fan-out/merge: the unioned releases plus any sources that hit their request limit.</summary>
/// <param name="Releases">Merged, source-tagged release set (union across all responding sources).</param>
/// <param name="RateLimitedSources">Names of sources that failed with <see cref="RequestLimitReachedException"/>.</param>
public sealed record MergeResult(IReadOnlyList<RenderedRelease> Releases, IReadOnlyList<string> RateLimitedSources);
