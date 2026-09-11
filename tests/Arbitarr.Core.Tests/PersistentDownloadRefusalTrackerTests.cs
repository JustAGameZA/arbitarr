using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-v3w: the persisting tier over <see cref="DownloadRefusalTracker"/>. These tests pin the two
/// things the decorator is responsible for and the in-memory tracker is not: that writes are
/// MIRRORED to the store with the instants the tracker resolved, and that a store failure degrades
/// to the pre-arb-v3w behaviour instead of propagating to the download proxy.
/// </summary>
public class PersistentDownloadRefusalTrackerTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An in-memory <see cref="IDownloadRefusalStore"/>. Deliberately not a mock: the rehydration
    /// assertions below need a store that genuinely round-trips, and the upsert/delete semantics it
    /// implements are the same one-row-per-source contract the real one is pinned to.
    /// </summary>
    private sealed class FakeStore : IDownloadRefusalStore
    {
        private readonly Dictionary<string, DownloadRefusal> _rows = new(StringComparer.Ordinal);

        public int UpsertCount { get; private set; }

        public int DeleteCount { get; private set; }

        public Exception? FailWith { get; set; }

        public Task UpsertAsync(DownloadRefusal refusal, CancellationToken cancellationToken = default)
        {
            UpsertCount++;
            if (FailWith is not null)
            {
                return Task.FromException(FailWith);
            }

            _rows[refusal.SourceName] = refusal;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (FailWith is not null)
            {
                return Task.FromException(FailWith);
            }

            _rows.Remove(sourceName);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DownloadRefusal>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            if (FailWith is not null)
            {
                return Task.FromException<IReadOnlyList<DownloadRefusal>>(FailWith);
            }

            IReadOnlyList<DownloadRefusal> rows = _rows.Values
                .OrderBy(r => r.SourceName, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(rows);
        }
    }

    private static PersistentDownloadRefusalTracker CreateTracker(FakeStore store) =>
        new(new DownloadRefusalTracker(), async (operation, token) => await operation(store, token));

    [Fact]
    public async Task A_recorded_refusal_is_written_through_to_the_store()
    {
        var store = new FakeStore();
        var tracker = CreateTracker(store);

        await tracker.RecordRefusalAsync("nzbhydra2", "refused", At);

        var persisted = Assert.Single(await store.LoadAllAsync());
        Assert.Equal("nzbhydra2", persisted.SourceName);
        Assert.Equal(At, persisted.ObservedSinceUtc);
        Assert.Equal(At, persisted.LastObservedUtc);
    }

    /// <summary>
    /// The decorator must persist the instants the INNER TRACKER resolved, not the ones the caller
    /// passed. On a repeat those differ — the caller passes only the new instant, and the tracker
    /// preserves the original ObservedSinceUtc — so a decorator that wrote its own arguments through
    /// would persist a window that resets on every retry while the dashboard showed the correct one.
    /// The two would then disagree across a restart, which is the worst version of this bug.
    /// </summary>
    [Fact]
    public async Task A_repeat_persists_the_preserved_observed_since_instant_not_the_new_one()
    {
        var store = new FakeStore();
        var tracker = CreateTracker(store);
        var later = At.AddMinutes(30);

        await tracker.RecordRefusalAsync("nzbhydra2", "first", At);
        await tracker.RecordRefusalAsync("nzbhydra2", "second", later);

        var persisted = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(At, persisted.ObservedSinceUtc);
        Assert.Equal(later, persisted.LastObservedUtc);
        Assert.Equal("second", persisted.Reason);

        // And the in-memory view agrees, so there is one answer rather than two.
        var inMemory = Assert.Single(tracker.Snapshot());
        Assert.Equal(persisted.ObservedSinceUtc, inMemory.ObservedSinceUtc);
        Assert.Equal(persisted.LastObservedUtc, inMemory.LastObservedUtc);
    }

    [Fact]
    public async Task A_successful_grab_deletes_the_persisted_row()
    {
        var store = new FakeStore();
        var tracker = CreateTracker(store);
        await tracker.RecordRefusalAsync("nzbhydra2", "refused", At);
        Assert.Single(await store.LoadAllAsync());

        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");

        Assert.Empty(await store.LoadAllAsync());
        Assert.Empty(tracker.Snapshot());
    }

    /// <summary>
    /// THE REASON THIS BEAD EXISTS. A fresh tracker over a store that already holds a refusal must
    /// report it immediately, with the instants from the STORE — not from this process's start.
    /// </summary>
    [Fact]
    public async Task Rehydration_restores_a_persisted_refusal_with_its_original_instants()
    {
        var store = new FakeStore();
        var later = At.AddHours(6);
        await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "Refused HTTP 302: the source redirected instead of serving the file.", At, later));

        var tracker = CreateTracker(store);
        Assert.Empty(tracker.Snapshot());

        await tracker.RehydrateAsync();

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", refusal.SourceName);
        Assert.Contains("302", refusal.Reason);
        Assert.Equal(At, refusal.ObservedSinceUtc);
        Assert.Equal(later, refusal.LastObservedUtc);
    }

    [Fact]
    public async Task Rehydration_restores_a_refusal_never_yet_repeated()
    {
        // The LastObservedUtc == ObservedSinceUtc case takes the single-call arm of the replay, so it
        // is exercised separately from the two-call one above.
        var store = new FakeStore();
        await store.UpsertAsync(new DownloadRefusal("nzbhydra2", "refused", At, At));

        var tracker = CreateTracker(store);
        await tracker.RehydrateAsync();

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal(At, refusal.ObservedSinceUtc);
        Assert.Equal(At, refusal.LastObservedUtc);
    }

    [Fact]
    public async Task Rehydration_over_an_empty_store_reports_nothing()
    {
        var tracker = CreateTracker(new FakeStore());

        await tracker.RehydrateAsync();

        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public async Task Rehydration_restores_every_source_ordered_by_name()
    {
        var store = new FakeStore();
        await store.UpsertAsync(new DownloadRefusal("zeta", "refused", At, At));
        await store.UpsertAsync(new DownloadRefusal("alpha", "refused", At, At));

        var tracker = CreateTracker(store);
        await tracker.RehydrateAsync();

        Assert.Equal(new[] { "alpha", "zeta" }, tracker.Snapshot().Select(r => r.SourceName).ToArray());
    }

    /// <summary>
    /// A store write failure must not reach the download proxy. The item is still tracked in memory —
    /// exactly the pre-arb-v3w behaviour — so the operator still sees the banner for this process's
    /// lifetime; only its durability is lost.
    /// </summary>
    [Fact]
    public async Task A_failed_store_write_does_not_throw_and_leaves_the_item_tracked_in_memory()
    {
        var store = new FakeStore { FailWith = new InvalidOperationException("database is locked") };
        var tracker = CreateTracker(store);

        await tracker.RecordRefusalAsync("nzbhydra2", "refused", At);

        // Positive control: the write was genuinely ATTEMPTED, so the absence of a throw is the
        // swallow working rather than the store never having been called at all.
        Assert.Equal(1, store.UpsertCount);
        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", refusal.SourceName);
    }

    [Fact]
    public async Task A_failed_store_delete_does_not_throw_and_leaves_the_item_cleared_in_memory()
    {
        var store = new FakeStore();
        var tracker = CreateTracker(store);
        await tracker.RecordRefusalAsync("nzbhydra2", "refused", At);

        store.FailWith = new InvalidOperationException("database is locked");
        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");

        Assert.Equal(1, store.DeleteCount);
        Assert.Empty(tracker.Snapshot());
    }

    /// <summary>
    /// Rehydration, unlike the write paths, does NOT swallow: its caller is the startup service,
    /// which must be able to tell an empty database from an unreadable one in order to log it.
    /// </summary>
    [Fact]
    public async Task A_failed_rehydration_surfaces_to_the_caller()
    {
        var store = new FakeStore { FailWith = new InvalidOperationException("database is unreadable") };
        var tracker = CreateTracker(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.RehydrateAsync());
    }

    [Fact]
    public async Task Snapshot_reads_only_memory_and_never_queries_the_store()
    {
        // Reads are on the /api/status request path. A store that fails every call must therefore
        // still let Snapshot answer — which it can only do by not consulting the store at all.
        var store = new FakeStore();
        var tracker = CreateTracker(store);
        await tracker.RecordRefusalAsync("nzbhydra2", "refused", At);

        store.FailWith = new InvalidOperationException("the store must not be consulted here");

        Assert.Single(tracker.Snapshot());
    }
}
