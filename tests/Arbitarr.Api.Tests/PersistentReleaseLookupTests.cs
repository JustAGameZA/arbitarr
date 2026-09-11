using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-tps: the two-tier lookup's own behaviour — memory first, store on a miss, memory
/// repopulated from a store hit. The counting delegate below is what makes "repopulated" an
/// assertion rather than a claim: without it, a second call that happened to re-read the store
/// would look identical to one served from memory.
/// </summary>
public sealed class PersistentReleaseLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static ReleaseCandidate Candidate(string guid) => new()
    {
        Title = "Some.Release.S01E01.1080p",
        Guid = guid,
        PubDate = Now - TimeSpan.FromHours(1),
        Size = 987_654_321,
        Link = new Uri("https://indexer.example.invalid/get/abc"),
        Protocol = ProtocolKind.Usenet,
    };

    /// <summary>
    /// A counting stand-in for the durable store: records how many lookups reached it, so the test
    /// can distinguish "answered from memory" from "answered from the store again".
    /// </summary>
    private sealed class CountingStore
    {
        private readonly Dictionary<string, StoredRelease> _rows = new(StringComparer.Ordinal);

        public int FindCallCount { get; private set; }

        public void Add(StoredRelease release) => _rows[release.ProxyGuid] = release;

        public Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken)
        {
            FindCallCount++;
            return Task.FromResult(_rows.GetValueOrDefault(proxyGuid));
        }
    }

    /// <summary>
    /// THE RESTART CASE, at unit scale: memory knows nothing (as after a restart, or after its
    /// 30-minute TTL), the store does, and the lookup resolves. Before arb-tps this answered null
    /// and the download proxy turned it into a 404.
    /// </summary>
    [Fact]
    public async Task A_memory_miss_falls_back_to_the_store()
    {
        var memory = new InMemoryReleaseLookup(new FakeTimeProvider(Now));
        var store = new CountingStore();
        store.Add(new StoredRelease("proxy-1", "hydra", Candidate("upstream-1")));
        var lookup = new PersistentReleaseLookup(memory, store.FindAsync);

        // Memory alone does not know it — establishing the fallback below is what resolved it,
        // rather than the release having been in memory all along.
        Assert.Null(await memory.FindAsync("proxy-1"));

        var found = await lookup.FindAsync("proxy-1");

        Assert.NotNull(found);
        Assert.Equal("hydra", found.SourceName);
        Assert.Equal("upstream-1", found.Candidate.Guid);
        Assert.Equal(1, store.FindCallCount);
    }

    /// <summary>
    /// A store hit repopulates memory, so a retry (an *arr whose first fetch failed) is answered
    /// without a second database round trip. Asserted by the store's call COUNT staying at one —
    /// asserting only that the second call succeeded would pass just as happily if it had gone back
    /// to the store every time.
    /// </summary>
    [Fact]
    public async Task A_store_hit_repopulates_memory_so_the_next_lookup_skips_the_store()
    {
        var memory = new InMemoryReleaseLookup(new FakeTimeProvider(Now));
        var store = new CountingStore();
        store.Add(new StoredRelease("proxy-2", "hydra", Candidate("upstream-2")));
        var lookup = new PersistentReleaseLookup(memory, store.FindAsync);

        Assert.NotNull(await lookup.FindAsync("proxy-2"));
        Assert.Equal(1, store.FindCallCount);

        Assert.NotNull(await lookup.FindAsync("proxy-2"));
        Assert.Equal(1, store.FindCallCount);

        // And the repopulation is real rather than an internal cache of this type: the memory tier
        // itself now answers, which is what the classifier worker's Snapshot() also sees.
        Assert.NotNull(await memory.FindAsync("proxy-2"));
    }

    /// <summary>
    /// A memory HIT never reaches the store. This is the zero-DB hot path the download proxy's own
    /// doc comment promises, and the reason the in-memory tier was kept rather than retired.
    /// </summary>
    [Fact]
    public async Task A_memory_hit_never_reaches_the_store()
    {
        var memory = new InMemoryReleaseLookup(new FakeTimeProvider(Now));
        var release = new RenderedRelease("hydra", Candidate("upstream-3"));
        memory.Record(release);
        var store = new CountingStore();
        var lookup = new PersistentReleaseLookup(memory, store.FindAsync);

        Assert.NotNull(await lookup.FindAsync(release.ProxyGuid));
        Assert.Equal(0, store.FindCallCount);
    }

    /// <summary>Unknown in both tiers is a miss, which the download proxy answers as 404.</summary>
    [Fact]
    public async Task Unknown_in_both_tiers_resolves_to_null()
    {
        var memory = new InMemoryReleaseLookup(new FakeTimeProvider(Now));
        var store = new CountingStore();
        var lookup = new PersistentReleaseLookup(memory, store.FindAsync);

        Assert.Null(await lookup.FindAsync("never-recorded"));
        Assert.Equal(1, store.FindCallCount);
    }

    /// <summary>
    /// A store that THROWS is answered as a miss, not as an exception out of the download proxy.
    /// The durable tier is an improvement on memory-only behaviour, so its failure must degrade to
    /// that behaviour (a 404) rather than to a 500 — which would be strictly worse than before this
    /// type existed.
    /// </summary>
    [Fact]
    public async Task A_failing_store_degrades_to_a_miss_rather_than_throwing()
    {
        var memory = new InMemoryReleaseLookup(new FakeTimeProvider(Now));
        var lookup = new PersistentReleaseLookup(
            memory,
            (_, _) => throw new InvalidOperationException("database unavailable"));

        Assert.Null(await lookup.FindAsync("proxy-4"));
    }

    /// <summary>
    /// A cancellation is NOT swallowed by the catch above. A caller that disconnected must surface
    /// as a cancellation rather than being reported as "this release does not exist" — the broad
    /// catch is there for a failing database, and this pins that it did not also eat this.
    /// </summary>
    [Fact]
    public async Task A_cancellation_propagates_rather_than_being_reported_as_a_miss()
    {
        var memory = new InMemoryReleaseLookup(new FakeTimeProvider(Now));
        var lookup = new PersistentReleaseLookup(
            memory,
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<StoredRelease?>(null);
            });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lookup.FindAsync("proxy-5", cts.Token));
    }
}
