using System.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-x7w8.5. Proves what a refresh actually does to the store: writes a fetched result under the
/// aggregator's own key convention for BOTH protocol families, and leaves a previously stored entry
/// alone when the fetch fails.
/// </summary>
public class CapsRefresherTests
{
    private sealed class InMemoryCapsCacheStore : ICapsCacheStore
    {
        private readonly Dictionary<string, SourceCaps> _store = new(StringComparer.Ordinal);

        /// <summary>How many times <see cref="SaveAsync"/> was called, for asserting a non-write.</summary>
        public int SaveCount { get; private set; }

        public Task<SourceCaps?> GetLastKnownGoodAsync(string sourceName, CancellationToken cancellationToken = default)
            => Task.FromResult(_store.TryGetValue(sourceName, out var caps) ? caps : null);

        public Task SaveAsync(string sourceName, SourceCaps caps, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            _store[sourceName] = caps;
            return Task.CompletedTask;
        }

        // Mirrors the real store: removes the several protocol keys the bare name expands into,
        // never a prefix match, so a double is not kinder than the thing it stands in for.
        public Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default)
        {
            foreach (var protocol in CapsAggregator.AllProtocols)
            {
                _store.Remove(CapsAggregator.CacheKey(sourceName, protocol));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpstreamSource : IUpstreamSource
    {
        private readonly Func<SearchProtocol, CancellationToken, Task<SourceCaps>> _getCaps;

        public FakeUpstreamSource(string name, Func<SearchProtocol, Task<SourceCaps>> getCaps)
            : this(name, (protocol, _) => getCaps(protocol))
        {
        }

        /// <summary>
        /// The token-aware overload, for the ceiling tests. A fake that ignored the token could not
        /// model an upstream that hangs until something cancels it — it would either return at once
        /// or block forever — so the ceiling could not be observed doing anything.
        /// </summary>
        public FakeUpstreamSource(string name, Func<SearchProtocol, CancellationToken, Task<SourceCaps>> getCaps)
        {
            Name = name;
            _getCaps = getCaps;
        }

        public string Name { get; }

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default)
            => _getCaps(protocol, cancellationToken);

        public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// A refresh writes BOTH protocol families, each under its own key. Keyed by protocol since #99
    /// because the two endpoints answer differently, so refreshing one and reporting success would
    /// leave the other family's entry to go stale unnoticed — the entry a later fallback would serve.
    ///
    /// <para>The stored payloads are asserted to DIFFER by content, not merely to exist: two rows
    /// holding the same caps is what a key that ignored the protocol would produce, and a
    /// both-keys-present assertion alone passes for that.</para>
    /// </summary>
    [Fact]
    public async Task RefreshAsync_StoresBothProtocolFamilies_UnderTheirOwnKeys()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store);

        // Each endpoint answers with a category only it has, mirroring NZBHydra2's split.
        var source = new FakeUpstreamSource("indexer-1", protocol => Task.FromResult(
            protocol == SearchProtocol.Torznab
                ? new SourceCaps(new[] { 5000 }, true, false, 100)
                : new SourceCaps(new[] { 2000 }, false, true, 100)));

        var outcomes = await refresher.RefreshAsync(source);

        Assert.All(outcomes, o => Assert.True(o.Refreshed));
        // Ordered by the enum's own numeric value (Torznab = 1, Newznab = 2). Asserted as an ordered
        // sequence rather than as two Assert.Contains calls, so a refresher that visited one family
        // twice and the other not at all would fail here rather than satisfying both containments.
        Assert.Equal(
            new[] { SearchProtocol.Torznab, SearchProtocol.Newznab },
            outcomes.Select(o => o.Protocol).OrderBy(p => p));
        Assert.All(outcomes, o => Assert.Equal("indexer-1", o.SourceName));

        var torznab = await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("indexer-1", SearchProtocol.Torznab));
        var newznab = await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("indexer-1", SearchProtocol.Newznab));

        Assert.Equal(new[] { 5000 }, torznab!.SupportedCategories);
        Assert.Equal(new[] { 2000 }, newznab!.SupportedCategories);
    }

    /// <summary>
    /// The stored entry is REPLACED by a successful refresh — this is what makes the action worth
    /// offering rather than a no-op over an already-populated store.
    ///
    /// <para>Seeded with a value the refresh cannot coincidentally reproduce (category 9999), so
    /// "the stored entry now holds the fetched caps" cannot pass because the seed already matched.</para>
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ReplacesThePreviouslyStoredEntry()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store);
        var key = CapsAggregator.CacheKey("indexer-1", SearchProtocol.Torznab);

        await store.SaveAsync(key, new SourceCaps(new[] { 9999 }, false, false, 10));
        var seeded = await store.GetLastKnownGoodAsync(key);
        Assert.Equal(new[] { 9999 }, seeded!.SupportedCategories); // positive control: the seed took

        var source = new FakeUpstreamSource(
            "indexer-1",
            _ => Task.FromResult(new SourceCaps(new[] { 5000, 5030 }, true, false, 100)));

        await refresher.RefreshAsync(source);

        var stored = await store.GetLastKnownGoodAsync(key);
        Assert.Equal(new[] { 5000, 5030 }, stored!.SupportedCategories);
        Assert.DoesNotContain(9999, stored.SupportedCategories);
    }

    /// <summary>
    /// A failed fetch writes NOTHING and leaves the previous entry intact. A refresh that blanked the
    /// store on failure would defeat the aggregator's last-known-good fallback on exactly the
    /// schedule meant to keep it fresh — the background pass would erase the fallback the first time
    /// an indexer was down.
    ///
    /// <para>Both halves are asserted: the entry still reads back unchanged, AND no save was
    /// attempted. The read alone would pass if the implementation had written the SAME value back.</para>
    /// </summary>
    [Fact]
    public async Task RefreshAsync_FailedFetch_WritesNothingAndLeavesTheStoredEntryIntact()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store);
        var key = CapsAggregator.CacheKey("indexer-1", SearchProtocol.Torznab);

        await store.SaveAsync(key, new SourceCaps(new[] { 5000 }, true, false, 100));
        var savesBefore = store.SaveCount;

        var source = new FakeUpstreamSource(
            "indexer-1",
            _ => throw new HttpRequestException("simulated: unreachable"));

        var outcomes = await refresher.RefreshAsync(source);

        Assert.All(outcomes, o => Assert.False(o.Refreshed));
        Assert.Equal(savesBefore, store.SaveCount);

        var stored = await store.GetLastKnownGoodAsync(key);
        Assert.Equal(new[] { 5000 }, stored!.SupportedCategories);
    }

    /// <summary>
    /// The two families are independent: an indexer that answers torznab and fails newznab — a
    /// torrent-only tracker — refreshes the half it has. Reporting the whole refresh as failed there
    /// would train an operator to ignore the indicator.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_OneFamilyFailing_DoesNotStopTheOther()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store);

        var source = new FakeUpstreamSource("torrent-only", protocol => protocol == SearchProtocol.Torznab
            ? Task.FromResult(new SourceCaps(new[] { 5000 }, true, false, 100))
            : throw new HttpRequestException("simulated: no newznab endpoint here"));

        var outcomes = await refresher.RefreshAsync(source);

        Assert.True(outcomes.Single(o => o.Protocol == SearchProtocol.Torznab).Refreshed);
        Assert.False(outcomes.Single(o => o.Protocol == SearchProtocol.Newznab).Refreshed);

        Assert.NotNull(await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("torrent-only", SearchProtocol.Torznab)));
        Assert.Null(await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("torrent-only", SearchProtocol.Newznab)));
    }

    /// <summary>
    /// The background pass's property: one dead source does not stop the rest. At N=3 with the
    /// FIRST source failing, both later sources must still be refreshed — a loop that abandoned the
    /// pass on the first failure would leave everything after it stale while looking like it ran.
    /// </summary>
    [Fact]
    public async Task RefreshAllAsync_AtThreeSources_OneFailing_StillRefreshesTheOthers()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store);

        var dead = new FakeUpstreamSource("dead", _ => throw new HttpRequestException("simulated"));
        var healthyOne = new FakeUpstreamSource(
            "healthy-1", _ => Task.FromResult(new SourceCaps(new[] { 5000 }, true, false, 100)));
        var healthyTwo = new FakeUpstreamSource(
            "healthy-2", _ => Task.FromResult(new SourceCaps(new[] { 2000 }, false, true, 100)));

        var outcomes = await refresher.RefreshAllAsync(new IUpstreamSource[] { dead, healthyOne, healthyTwo });

        // Three sources, two families each.
        Assert.Equal(6, outcomes.Count);
        Assert.All(outcomes.Where(o => o.SourceName == "dead"), o => Assert.False(o.Refreshed));
        Assert.All(outcomes.Where(o => o.SourceName != "dead"), o => Assert.True(o.Refreshed));

        Assert.NotNull(await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("healthy-1", SearchProtocol.Torznab)));
        Assert.NotNull(await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("healthy-2", SearchProtocol.Newznab)));
        Assert.Null(await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("dead", SearchProtocol.Torznab)));
    }

    /// <summary>
    /// A cancelled token is not an upstream failure. The catch is deliberately narrowed by
    /// <c>when (cancellationToken.IsCancellationRequested is false)</c>, so a stopping host
    /// propagates rather than being reported to an operator as "indexer unreachable".
    /// </summary>
    [Fact]
    public async Task RefreshAsync_CancellationPropagates_RatherThanReadingAsAFailedFetch()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store);
        using var cts = new CancellationTokenSource();

        var source = new FakeUpstreamSource("indexer-1", _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => refresher.RefreshAsync(source, cts.Token));
    }

    // ---------- The per-source ceiling ----------

    /// <summary>
    /// THE GIVE-UP PATH. An upstream that accepts the call and never answers must not hold the
    /// refresh open: the ceiling fires, both families report not-refreshed, and nothing is written.
    ///
    /// <para>This is the regression the ceiling exists for. Before it, the inline refresh on a
    /// source create/update was bounded only by the per-row <c>HttpClient.Timeout</c> — an
    /// operator-configurable value — so a source configured with a large timeout against an
    /// unroutable address held the whole request open for it. In CI that overran the test client's
    /// own 100s timeout and failed the run.</para>
    ///
    /// <para>The ceiling is injected in MILLISECONDS, which is the entire reason it is a constructor
    /// parameter rather than a constant: the give-up behaviour is proven in the time it takes to hit
    /// it, not in the ten real seconds the default would cost. The fake hangs until ITS OWN token is
    /// cancelled — i.e. until the ceiling cancels it — so if the ceiling were never applied this test
    /// would hang rather than fail, and the assertion below could not pass by accident.</para>
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheUpstreamNeverAnswers_GivesUpAtTheCeiling_AndWritesNothing()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store, TimeSpan.FromMilliseconds(50));

        var source = new FakeUpstreamSource("hangs", async (protocol, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new UnreachableException("The delay above only ever ends by cancellation.");
        });

        var outcomes = await refresher.RefreshAsync(source);

        // Both families attempted and both reported — a ceiling that abandoned the loop would return
        // one entry, leaving the other family silently unreported rather than reported as failed.
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.False(o.Refreshed));

        // The give-up wrote nothing, so a previously stored entry would have survived it.
        Assert.Equal(0, store.SaveCount);
    }

    /// <summary>
    /// POSITIVE CONTROL for the test above (CLAUDE.md §4). "Nothing was written" passes just as
    /// happily when the refresher writes NOTHING EVER — a ceiling of zero, a broken store double, or
    /// a fetch loop that never ran would all leave <c>SaveCount</c> at 0 and leave the give-up test
    /// green while proving nothing about the ceiling.
    ///
    /// <para>So this drives the SAME refresher configuration with the same kind of source and the
    /// only difference that matters — the fetch completes inside the ceiling instead of outside it —
    /// and asserts the opposite outcome: both families refreshed, both entries present. Together the
    /// pair establishes that the ceiling discriminates, rather than that this code path always
    /// fails.</para>
    ///
    /// <para>The delay is real but an order of magnitude inside the ceiling, so the two tests differ
    /// by which side of it they land on and by nothing else.</para>
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheUpstreamAnswersInsideTheCeiling_StillWritesBothFamilies()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store, TimeSpan.FromSeconds(5));

        var source = new FakeUpstreamSource("prompt", async (protocol, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
            return protocol == SearchProtocol.Torznab
                ? new SourceCaps(new[] { 5000 }, true, false, 100)
                : new SourceCaps(new[] { 2000 }, false, true, 100);
        });

        var outcomes = await refresher.RefreshAsync(source);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Refreshed));
        Assert.Equal(2, store.SaveCount);

        Assert.NotNull(await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("prompt", SearchProtocol.Torznab)));
        Assert.NotNull(await store.GetLastKnownGoodAsync(
            CapsAggregator.CacheKey("prompt", SearchProtocol.Newznab)));
    }

    /// <summary>
    /// The ceiling must not be mistakable for CALLER cancellation. When the caller's own token is
    /// still live, a ceiling hit is an ordinary not-refreshed outcome and never an exception — which
    /// is what lets an operator's save succeed against a dead indexer.
    ///
    /// <para>The distinction is load-bearing and easy to break: the fetch runs on the LINKED token,
    /// which the ceiling also cancels, so a catch filter written against that token instead of the
    /// caller's would rethrow every ceiling hit as if the host were shutting down — turning the
    /// bounded give-up back into a failed request. This asserts the sibling behaviour the existing
    /// cancellation test asserts from the other side: there, a cancelled CALLER token propagates;
    /// here, a cancelled CEILING token does not.</para>
    /// </summary>
    [Fact]
    public async Task RefreshAsync_ACeilingHit_IsNotReportedAsCallerCancellation()
    {
        var store = new InMemoryCapsCacheStore();
        var refresher = new CapsRefresher(store, TimeSpan.FromMilliseconds(50));

        using var callerTokenSource = new CancellationTokenSource();

        var source = new FakeUpstreamSource("hangs", async (protocol, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new UnreachableException("The delay above only ever ends by cancellation.");
        });

        // No throw: the ceiling fired, the caller's token did not.
        var outcomes = await refresher.RefreshAsync(source, callerTokenSource.Token);

        Assert.All(outcomes, o => Assert.False(o.Refreshed));

        // And the caller's token is untouched by our ceiling — a ceiling that cancelled the caller's
        // own token would abort everything else that token governs, not just this refresh.
        Assert.False(callerTokenSource.IsCancellationRequested);
    }
}
