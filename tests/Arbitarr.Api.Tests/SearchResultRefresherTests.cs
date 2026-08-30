using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Exercises <see cref="SearchResultRefresher"/>, the <see cref="RefreshFetcher"/> the Host hands
/// the proactive <see cref="RefreshWorker"/>: it must re-run the query persisted inside a stale
/// entry, and must return null (leaving the entry untouched, M3-10) whenever the payload cannot be
/// read or the merge came back degraded and empty.
/// </summary>
public class SearchResultRefresherTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static ReleaseCandidate MakeCandidate(string guid) => new()
    {
        Title = $"Release {guid}",
        Guid = guid,
        PubDate = TestReleases.FixedPubDate,
        Size = 1000,
        Link = new Uri($"http://192.0.2.40:8080/get/{guid}"),
        Category = new[] { 5000 },
        Protocol = ProtocolKind.Torrent,
    };

    private static CachedSearchResult MakeEntry(string payloadJson) =>
        new("query-key", payloadJson, Start, Start.AddMinutes(15), Start.AddDays(7), Start);

    [Fact]
    public async Task Refresh_re_runs_the_stored_query_and_returns_a_payload_carrying_the_same_query()
    {
        var query = new SearchQuery("bleach", new[] { 5000 }, 50, 0, TvdbId: 74796, Season: 17, Episode: 36);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var source = new FakeUpstreamSource("eztv", searchResults: new[] { MakeCandidate("fresh") });
        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { source }));

        var payloadJson = await refresher.RefreshAsync(entry);

        Assert.NotNull(payloadJson);
        var payload = CachedSearchPayload.Deserialize(payloadJson!);
        Assert.NotNull(payload);

        // Field-wise rather than record equality: Categories is an IReadOnlyList<int>, so the
        // synthesized record equality compares by reference and a JSON round-trip (int[] -> List<int>)
        // would never match despite every value surviving intact.
        Assert.Equal(query.QueryText, payload!.Query.QueryText);
        Assert.Equal(query.Categories, payload.Query.Categories);
        Assert.Equal(query.Limit, payload.Query.Limit);
        Assert.Equal(query.Offset, payload.Query.Offset);
        Assert.Equal(query.TvdbId, payload.Query.TvdbId);
        Assert.Equal(query.TmdbId, payload.Query.TmdbId);
        Assert.Equal(query.Season, payload.Query.Season);
        Assert.Equal(query.Episode, payload.Query.Episode);
        Assert.Equal("fresh", Assert.Single(payload.Releases).Candidate.Guid);
    }

    [Fact]
    public async Task Refresh_returns_null_when_the_stored_payload_cannot_be_read()
    {
        var source = new FakeUpstreamSource("eztv", searchResults: new[] { MakeCandidate("fresh") });
        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { source }));

        Assert.Null(await refresher.RefreshAsync(MakeEntry("{not valid json")));
    }

    [Fact]
    public async Task Refresh_returns_null_when_the_merge_is_empty_and_the_source_was_rate_limited()
    {
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var source = new FakeUpstreamSource("eztv", searchException: new RequestLimitReachedException());
        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { source }));

        Assert.Null(await refresher.RefreshAsync(entry));
    }

    [Fact]
    public async Task Refresh_returns_an_empty_payload_when_a_healthy_source_genuinely_has_no_results()
    {
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { new FakeUpstreamSource("eztv") }));

        var payloadJson = await refresher.RefreshAsync(entry);

        Assert.NotNull(payloadJson);
        Assert.Empty(CachedSearchPayload.Deserialize(payloadJson!)!.Releases);
    }
}
