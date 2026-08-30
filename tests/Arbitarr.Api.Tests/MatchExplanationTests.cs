using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Exercises <see cref="MatchExplanationEndpoint"/> (M7-9, AC26a UI half): a known proxy guid
/// returns the upstream-reported title (<see cref="ReleaseCandidate.OriginalTitle"/>) side by side
/// with the title actually used for matching (<see cref="ReleaseCandidate.Title"/>, AC-M7b); an
/// unknown proxy guid 404s, matching <see cref="DownloadProxyEndpoint"/>'s convention for the same
/// id space.
/// </summary>
public class MatchExplanationTests
{
    [Fact]
    public async Task Known_proxy_guid_returns_title_and_original_title_side_by_side()
    {
        // Simulates a release whose title was rewritten post-ingest (M5 normalizer): Title holds
        // the rewritten form, OriginalTitleRaw preserves exactly what the source reported.
        var release = new RenderedRelease("eztv", new ReleaseCandidate
        {
            Title = "Bleach S17E45",
            OriginalTitleRaw = "Bleach - Sennen Kessen-hen 45",
            Guid = "123",
            PubDate = TestReleases.FixedPubDate,
            Link = new Uri("http://192.0.2.21:5076/gettorrent/api/123"),
        });
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var result = await MatchExplanationEndpoint.HandleAsync(release.ProxyGuid, lookup, CancellationToken.None);

        var okResult = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<MatchExplanationResponse>>(result);
        Assert.Equal("Bleach S17E45", okResult.Value!.Title);
        Assert.Equal("Bleach - Sennen Kessen-hen 45", okResult.Value.OriginalTitle);
    }

    [Fact]
    public async Task Release_never_normalized_reports_identical_title_and_original_title()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "456", title: "Some.Release.Title");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var result = await MatchExplanationEndpoint.HandleAsync(release.ProxyGuid, lookup, CancellationToken.None);

        var okResult = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<MatchExplanationResponse>>(result);
        Assert.Equal("Some.Release.Title", okResult.Value!.Title);
        Assert.Equal("Some.Release.Title", okResult.Value.OriginalTitle);
    }

    [Fact]
    public async Task Unknown_proxy_guid_returns_not_found()
    {
        var lookup = new InMemoryReleaseLookup();

        var result = await MatchExplanationEndpoint.HandleAsync("does-not-exist", lookup, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }
}
