using System.Diagnostics;
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

        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new IUpstreamSource[] { sourceA, sourceB }));

        var result = await mergeStage.MergeAsync(new SearchQuery(null, Array.Empty<int>(), 50, SearchProtocol.Torznab), CancellationToken.None);

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

        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new IUpstreamSource[] { sourceA, rateLimited }));

        var result = await mergeStage.MergeAsync(new SearchQuery(null, Array.Empty<int>(), 50, SearchProtocol.Torznab), CancellationToken.None);

        Assert.Single(result.Releases);
        Assert.Equal("source-a-1", result.Releases[0].Candidate.Guid);
        Assert.Contains("source-limited", result.RateLimitedSources);
    }

    /// <summary>
    /// arb-x7w8.7 gap 1: one hung source must not hold the merge open past its OWN budget. The slow
    /// source is given a 20s delay behind a 200ms budget, so the delay is still pending when the
    /// merge returns; asserting the elapsed time is well under that delay is what makes this a
    /// statement about waiting rather than about ordering. The slow source sits FIRST in the
    /// resolved order, so a merge that serialised on its leg would fail this too.
    /// </summary>
    [Fact]
    public async Task MergeAsync_returns_without_waiting_for_a_source_that_outlives_its_own_budget()
    {
        var slow = new SecondFakeUpstreamSource(
            "source-slow",
            searchResults: new[] { MakeRelease("source-slow-1", "Never Delivered") },
            searchBudget: TimeSpan.FromMilliseconds(200),
            searchDelay: TimeSpan.FromSeconds(20));
        var fast = new SecondFakeUpstreamSource(
            "source-fast",
            searchResults: new[] { MakeRelease("source-fast-1", "Release From Fast Source") });

        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new IUpstreamSource[] { slow, fast }));

        var started = Stopwatch.StartNew();
        var result = await mergeStage.MergeAsync(new SearchQuery(null, Array.Empty<int>(), 50, SearchProtocol.Torznab), CancellationToken.None);
        started.Stop();

        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"merge took {started.Elapsed} — it waited on the slow source's 20s delay instead of its 200ms budget");

        // The survivor's results are intact, and the source that ran out of time is NAMED rather
        // than merely absent — absence alone being the whole story is what this bead exists to stop.
        Assert.Single(result.Releases);
        Assert.Equal("source-fast-1", result.Releases[0].Candidate.Guid);
        Assert.Equal(new[] { "source-slow" }, result.TimedOutSources);
    }

    /// <summary>
    /// arb-x7w8.7 gap 2, asserted PER SOURCE at N=3 with a DIFFERENT failure kind each, because
    /// "some source was reported" passes vacuously when only one add-only assertion is checked
    /// (CLAUDE.md §4). Each of the three lists is asserted by EQUALITY against its single expected
    /// name, which fails both when the right name is missing and when a wrong one is present — so
    /// every failing source is simultaneously the positive control for its own kind and a negative
    /// control for the other two. A fourth, healthy source is the positive control for the mechanism
    /// as a whole: it proves a source that SUCCEEDS is named in none of the lists, which an
    /// implementation that simply listed every resolved source would fail. Ordering puts a failing
    /// source in the middle so a first/last off-by-one in the classifier cannot pass.
    /// </summary>
    [Fact]
    public async Task MergeAsync_names_each_failing_source_in_the_list_matching_its_own_failure_kind()
    {
        var healthy = new SecondFakeUpstreamSource(
            "source-healthy",
            searchResults: new[] { MakeRelease("source-healthy-1", "Release From Healthy Source") });
        var timedOut = new SecondFakeUpstreamSource(
            "source-timeout",
            searchBudget: TimeSpan.FromMilliseconds(200),
            searchDelay: TimeSpan.FromSeconds(20));
        var rateLimited = new SecondFakeUpstreamSource("source-ratelimit", throwsRequestLimitReached: true);
        var transportFailed = new SecondFakeUpstreamSource(
            "source-transport",
            searchException: new HttpRequestException("connection refused"));

        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(
            new IUpstreamSource[] { timedOut, rateLimited, healthy, transportFailed }));

        var result = await mergeStage.MergeAsync(new SearchQuery(null, Array.Empty<int>(), 50, SearchProtocol.Torznab), CancellationToken.None);

        Assert.Equal(new[] { "source-timeout" }, result.TimedOutSources);
        Assert.Equal(new[] { "source-ratelimit" }, result.RateLimitedSources);
        Assert.Equal(new[] { "source-transport" }, result.FailedSources);

        // The healthy source is in none of the three, and its results still arrived. Without this,
        // the three assertions above would hold just as well for a merge that reported everything.
        Assert.DoesNotContain("source-healthy", result.TimedOutSources);
        Assert.DoesNotContain("source-healthy", result.RateLimitedSources);
        Assert.DoesNotContain("source-healthy", result.FailedSources);
        Assert.Single(result.Releases);
        Assert.Equal("source-healthy-1", result.Releases[0].Candidate.Guid);
    }

    /// <summary>
    /// arb-x7w8.7's load-bearing invariant: a partial merge is a protocol answer, not an
    /// infrastructure error (CONTEXT.md; see SearchEndpoint.InfrastructureErrorResult's remarks).
    /// At THIS level that means MergeAsync itself does not throw — with a majority of its sources
    /// down it still returns, still carries the survivor's releases, and names the rest. The
    /// endpoint's 200-vs-900 decision is a SearchEndpoint concern and out of this bead's scope.
    /// </summary>
    [Fact]
    public async Task MergeAsync_answers_with_results_rather_than_throwing_when_most_sources_are_down()
    {
        var healthy = new SecondFakeUpstreamSource(
            "source-healthy",
            searchResults: new[] { MakeRelease("source-healthy-1", "Release From Healthy Source") });
        var rateLimited = new SecondFakeUpstreamSource("source-ratelimit", throwsRequestLimitReached: true);
        var transportFailed = new SecondFakeUpstreamSource(
            "source-transport",
            searchException: new InvalidOperationException("malformed upstream payload"));

        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(
            new IUpstreamSource[] { rateLimited, healthy, transportFailed }));

        var result = await mergeStage.MergeAsync(new SearchQuery(null, Array.Empty<int>(), 50, SearchProtocol.Torznab), CancellationToken.None);

        Assert.Single(result.Releases);
        Assert.Equal("source-healthy-1", result.Releases[0].Candidate.Guid);
        Assert.Equal(new[] { "source-ratelimit" }, result.RateLimitedSources);
        Assert.Equal(new[] { "source-transport" }, result.FailedSources);
        Assert.Empty(result.TimedOutSources);
    }
}
