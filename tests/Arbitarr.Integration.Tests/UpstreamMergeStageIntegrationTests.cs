using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M1-10: exercises <see cref="UpstreamMergeStage"/> end-to-end with two independent, distinct
/// fake <see cref="IUpstreamSource"/> implementations, proving the merged/unioned release set
/// spans both sources with zero renderer changes (the XML rendering path itself is already
/// covered by Arbitarr.Api.Tests' golden-XML tests and is not what this test targets).
/// </summary>
public class UpstreamMergeStageIntegrationTests
{
    private static ReleaseCandidate MakeRelease(string guid, string title) => new()
    {
        Title = title,
        Guid = guid,
        PubDate = DateTimeOffset.UtcNow,
        Size = 123_456,
        Link = new Uri($"http://192.0.2.60:8080/getnzb/{guid}"),
        Category = new[] { 5000 },
        Protocol = ProtocolKind.Usenet,
    };

    [Fact]
    public async Task MergeAsync_unions_results_from_multiple_distinct_sources()
    {
        var sourceA = new SecondFakeUpstreamSource(
            "source-a",
            searchResults: new[] { MakeRelease("source-a-1", "Release From Source A") });
        var sourceB = new SecondFakeUpstreamSource(
            "source-b",
            searchResults: new[] { MakeRelease("source-b-1", "Release From Source B") });

        var mergeStage = new UpstreamMergeStage(new IUpstreamSource[] { sourceA, sourceB });

        var result = await mergeStage.MergeAsync(new SearchQuery(null, Array.Empty<int>(), 50), CancellationToken.None);

        Assert.Equal(2, result.Releases.Count);
        Assert.Contains(result.Releases, r => r.Candidate.Guid == "source-a-1");
        Assert.Contains(result.Releases, r => r.Candidate.Guid == "source-b-1");
        Assert.Empty(result.RateLimitedSources);
    }

    [Fact]
    public async Task MergeAsync_excludes_rate_limited_source_but_still_unions_the_other()
    {
        var sourceA = new SecondFakeUpstreamSource(
            "source-a",
            searchResults: new[] { MakeRelease("source-a-1", "Release From Source A") });
        var rateLimited = new SecondFakeUpstreamSource("source-limited", throwsRequestLimitReached: true);

        var mergeStage = new UpstreamMergeStage(new IUpstreamSource[] { sourceA, rateLimited });

        var result = await mergeStage.MergeAsync(new SearchQuery(null, Array.Empty<int>(), 50), CancellationToken.None);

        Assert.Single(result.Releases);
        Assert.Equal("source-a-1", result.Releases[0].Candidate.Guid);
        Assert.Contains("source-limited", result.RateLimitedSources);
    }
}
