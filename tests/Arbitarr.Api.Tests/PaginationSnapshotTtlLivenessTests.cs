using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// M7-8c/AC24: proves <see cref="PaginationSnapshotService"/>'s live-TTL constructor re-reads its
/// <see cref="ISnapshotTtlSource"/> on every <see cref="PaginationSnapshotService.GetPageAsync"/>
/// call that persists a snapshot, instead of capturing a fixed TTL at construction — mirroring
/// <c>Arbitarr.Core.Tests.RefreshWorkerOptionsLivenessTests</c>'s mutable-fake-source pattern.
/// </summary>
public class PaginationSnapshotTtlLivenessTests
{
    private sealed class MutableSnapshotTtlSource : ISnapshotTtlSource
    {
        public TimeSpan Ttl { get; set; }

        public ValueTask<TimeSpan> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Ttl);
    }

    private static IUpstreamSource MakeSourceWithReleases(string name, int count)
    {
        var releases = Enumerable.Range(0, count).Select(i => new ReleaseCandidate
        {
            Title = $"Release {i}",
            Guid = i.ToString(),
            PubDate = TestReleases.FixedPubDate,
            Size = 1000 + i,
            Link = new Uri($"http://192.0.2.40:8080/get/{i}"),
            Category = new[] { 5000 },
            Protocol = ProtocolKind.Torrent,
        }).ToArray();

        return new FakeUpstreamSource(name, searchResults: releases);
    }

    [Fact]
    public async Task A_ttl_changed_on_the_source_between_calls_is_observed_by_the_very_next_save_without_reconstruction()
    {
        var source = MakeSourceWithReleases("eztv", 10);
        var mergeStage = new UpstreamMergeStage(new[] { source });
        var store = new FakeQuerySnapshotStore();
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var ttlSource = new MutableSnapshotTtlSource { Ttl = TimeSpan.FromSeconds(60) };
        var service = new PaginationSnapshotService(mergeStage, TestCacheStage.Create(time), store, time, ttlSource);

        // First query: persists with the initial TTL.
        await service.GetPageAsync("search", new SearchQuery("bleach", Array.Empty<int>(), 5, 0));
        Assert.Equal(TimeSpan.FromSeconds(60), store.ObservedTtls[0]);

        // Change the setting-backed TTL, then trigger a second, distinct snapshot save (a
        // different query text so it is a fresh materialization, not a snapshot hit).
        ttlSource.Ttl = TimeSpan.FromSeconds(600);
        await service.GetPageAsync("search", new SearchQuery("naruto", Array.Empty<int>(), 5, 0));

        Assert.Equal(2, store.SaveCallCount);
        Assert.Equal(TimeSpan.FromSeconds(600), store.ObservedTtls[1]);
    }
}
