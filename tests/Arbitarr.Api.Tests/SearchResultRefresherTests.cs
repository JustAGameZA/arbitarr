using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Pipeline;
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
        var query = new SearchQuery("bleach", new[] { 5000 }, 50, SearchProtocol.Torznab, 0, TvdbId: 74796, Season: 17, Episode: 36);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var source = new FakeUpstreamSource("eztv", searchResults: new[] { MakeCandidate("fresh") });
        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { source }), new DedupStage(AllEqualSourcePriority.Instance));

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
        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { source }), new DedupStage(AllEqualSourcePriority.Instance));

        Assert.Null(await refresher.RefreshAsync(MakeEntry("{not valid json")));
    }

    [Fact]
    public async Task Refresh_returns_null_when_the_merge_is_empty_and_the_source_was_rate_limited()
    {
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50, SearchProtocol.Torznab);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var source = new FakeUpstreamSource("eztv", searchException: new RequestLimitReachedException());
        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { source }), new DedupStage(AllEqualSourcePriority.Instance));

        Assert.Null(await refresher.RefreshAsync(entry));
    }

    [Fact]
    public async Task Refresh_returns_an_empty_payload_when_a_healthy_source_genuinely_has_no_results()
    {
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50, SearchProtocol.Torznab);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var refresher = new SearchResultRefresher(new UpstreamMergeStage(new[] { new FakeUpstreamSource("eztv") }), new DedupStage(AllEqualSourcePriority.Instance));

        var payloadJson = await refresher.RefreshAsync(entry);

        Assert.NotNull(payloadJson);
        Assert.Empty(CachedSearchPayload.Deserialize(payloadJson!)!.Releases);
    }

    // --- The refresher is the SECOND writer of this row (arb-x7w8.8 review fixup) ----------------

    /// <summary>
    /// One release, carried by two differently-named sources: the pair a dedup group is made of.
    /// <paramref name="title"/> is a parameter so the same helper builds the non-matching control.
    /// </summary>
    private static ReleaseCandidate DuplicateOf(string sourceName, string title = "Bleach S17E36 1080p WEB-DL") => new()
    {
        Title = title,
        Guid = $"{sourceName}-guid",
        PubDate = TestReleases.FixedPubDate,
        Size = 1_000_000_000,
        Link = new Uri($"http://{sourceName}.example.invalid/get/1"),
        Category = new[] { 5000 },
        Protocol = ProtocolKind.Torrent,
    };

    private static async Task<CachedSearchPayload> RefreshTwoSourcesCarryingOneRelease()
    {
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50, SearchProtocol.Torznab);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var refresher = new SearchResultRefresher(
            new UpstreamMergeStage(new[]
            {
                new FakeUpstreamSource("alpha", searchResults: new[] { DuplicateOf("alpha") }),
                new FakeUpstreamSource("beta", searchResults: new[] { DuplicateOf("beta") }),
            }),
            new DedupStage(AllEqualSourcePriority.Instance));

        var payloadJson = await refresher.RefreshAsync(entry);
        Assert.NotNull(payloadJson);

        var payload = CachedSearchPayload.Deserialize(payloadJson!);
        Assert.NotNull(payload);
        return payload!;
    }

    /// <summary>
    /// <b>The refresher writes the same cache row <see cref="PaginationSnapshotService"/> writes, so
    /// it must write the same SHAPE.</b> Before this was fixed it serialised the raw merge, so the
    /// proactive worker silently replaced a grouped payload with an ungrouped one some minutes after
    /// it was written — off the request path, where nothing reports it, and only for the queries the
    /// worker happened to refresh.
    /// </summary>
    [Fact]
    public async Task A_refreshed_entry_is_still_grouped()
    {
        var payload = await RefreshTwoSourcesCarryingOneRelease();

        Assert.Single(payload.Releases);
    }

    /// <summary>
    /// The count-only assertion above is not enough on its own: a refresher that grouped correctly
    /// but dropped the loser would also write exactly one release. This asserts the group still
    /// HOLDS both members after the round trip through the cache row.
    /// </summary>
    [Fact]
    public async Task A_refreshed_group_retains_both_members()
    {
        var payload = await RefreshTwoSourcesCarryingOneRelease();

        Assert.Equal(2, 1 + payload.Releases[0].AlternateMembers.Count);
    }

    /// <summary>
    /// Per-member links survive the refresh and its serialisation, so a fallback grab against a
    /// member still resolves to that member's own source rather than the representative's (P9/P10).
    /// </summary>
    [Fact]
    public async Task A_refreshed_group_keeps_each_members_own_link()
    {
        var payload = await RefreshTwoSourcesCarryingOneRelease();
        var representative = payload.Releases[0];

        Assert.Equal(
            new[] { "http://alpha.example.invalid/get/1", "http://beta.example.invalid/get/1" },
            representative.AlternateMembers.Select(m => m.Candidate.Link.ToString())
                .Prepend(representative.Candidate.Link.ToString()).ToArray());
    }

    /// <summary>
    /// Per-member source names survive too — the name is what a priority lookup and an origin check
    /// are keyed on, so a member that kept its link but lost its name would still be unusable.
    /// </summary>
    [Fact]
    public async Task A_refreshed_group_keeps_each_members_own_source_name()
    {
        var payload = await RefreshTwoSourcesCarryingOneRelease();
        var representative = payload.Releases[0];

        Assert.Equal(
            new[] { "alpha", "beta" },
            representative.AlternateMembers.Select(m => m.SourceName).Prepend(representative.SourceName).ToArray());
    }

    /// <summary>
    /// The positive control for the four assertions above: the SAME two sources carrying releases
    /// that do NOT match come back as two ungrouped entries. Without this, a refresher that silently
    /// discarded one of every pair would satisfy "exactly one release" by accident, and the grouping
    /// assertions would be proving nothing.
    /// </summary>
    [Fact]
    public async Task Two_sources_carrying_DIFFERENT_releases_are_not_grouped_by_the_refresher()
    {
        var query = new SearchQuery("bleach", Array.Empty<int>(), 50, SearchProtocol.Torznab);
        var entry = MakeEntry(new CachedSearchPayload(query, Array.Empty<RenderedRelease>()).Serialize());

        var refresher = new SearchResultRefresher(
            new UpstreamMergeStage(new[]
            {
                new FakeUpstreamSource("alpha", searchResults: new[] { DuplicateOf("alpha") }),
                new FakeUpstreamSource("beta", searchResults: new[]
                {
                    DuplicateOf("beta", title: "Something Else Entirely S01E01"),
                }),
            }),
            new DedupStage(AllEqualSourcePriority.Instance));

        var payloadJson = await refresher.RefreshAsync(entry);

        Assert.Equal(2, CachedSearchPayload.Deserialize(payloadJson!)!.Releases.Count);
    }
}
