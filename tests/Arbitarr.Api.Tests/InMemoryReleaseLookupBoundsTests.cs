using Arbitarr.Api.Search;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// SEC-M3: <see cref="InMemoryReleaseLookup"/> must not grow without bound for the lifetime of a
/// long-running instance. These tests pin both bounding mechanisms: capacity-based eviction of the
/// oldest entry once <see cref="InMemoryReleaseLookup.MaxEntries"/> is exceeded, and TTL-based
/// expiry independent of capacity pressure.
/// </summary>
public class InMemoryReleaseLookupBoundsTests
{
    [Fact]
    public async Task Recording_beyond_MaxEntries_evicts_the_oldest_entry()
    {
        var lookup = new InMemoryReleaseLookup();

        var first = TestReleases.Torrent(guid: "first-guid");
        lookup.Record(first);

        for (var i = 0; i < InMemoryReleaseLookup.MaxEntries; i++)
        {
            lookup.Record(TestReleases.Torrent(guid: $"filler-{i}"));
        }

        var evicted = await lookup.FindAsync(first.ProxyGuid);
        Assert.Null(evicted);

        var mostRecent = TestReleases.Torrent(guid: $"filler-{InMemoryReleaseLookup.MaxEntries - 1}");
        var stillPresent = await lookup.FindAsync(mostRecent.ProxyGuid);
        Assert.NotNull(stillPresent);
    }

    [Fact]
    public async Task Entry_expires_after_EntryTtl_regardless_of_capacity()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var lookup = new InMemoryReleaseLookup(time);

        var release = TestReleases.Torrent(guid: "ttl-guid");
        lookup.Record(release);

        var beforeExpiry = await lookup.FindAsync(release.ProxyGuid);
        Assert.NotNull(beforeExpiry);

        time.Advance(InMemoryReleaseLookup.EntryTtl + TimeSpan.FromSeconds(1));

        var afterExpiry = await lookup.FindAsync(release.ProxyGuid);
        Assert.Null(afterExpiry);
    }
}
