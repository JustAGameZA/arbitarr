using Arbitarr.Core.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-x7w8.5: <see cref="CapsCacheStore.DeleteAsync"/> against the real table.
///
/// <para>A caps entry exists only to describe a configured source, so when that source is deleted
/// or renamed the entry must go with it (ADR 0016's set-membership rule). The key is the display
/// name, so an orphan is not merely dead weight: a later source created or renamed to that same
/// name would silently adopt the stale predecessor's categories as its own last-known-good.</para>
///
/// <para>Driven through the EF-backed store rather than a double, because the properties at risk
/// are properties of the QUERY — that it spans both protocol keys, and that it does not reach
/// beyond the named source — and a double cannot establish either about the real one.</para>
/// </summary>
public sealed class CapsCacheStoreDeleteTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arr-searcher-caps-cache-delete-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static SourceCaps CapsWith(int category) =>
        new(new[] { category }, SupportsTvSearch: true, SupportsMovieSearch: false, MaxPageSize: 100);

    /// <summary>
    /// Both of the named source's protocol entries go, and a SIBLING source's entries stay.
    ///
    /// <para>The sibling is what makes this test bite rather than merely pass. "Both entries are
    /// gone" is satisfied just as well by a delete that empties the whole table — and by a store
    /// that never wrote them in the first place, which is why the entries are asserted PRESENT
    /// before the delete rather than only absent after it. The sibling's name is also a PREFIX
    /// relationship with the target on purpose: keys are <c>{name}#{protocol}</c>, so an
    /// implementation that swept by name prefix instead of by built key would take the sibling with
    /// it and fail here, which is exactly the shortcut this guards.</para>
    /// </summary>
    [Fact]
    public async Task DeleteAsync_RemovesBothProtocolEntries_AndLeavesOtherSourcesAlone()
    {
        await using var context = CreateContext();
        var store = new CapsCacheStore(context);

        const string Target = "Hydra";
        const string Sibling = "Hydra (backup)";

        await store.SaveAsync(CapsAggregator.CacheKey(Target, SearchProtocol.Torznab), CapsWith(5000));
        await store.SaveAsync(CapsAggregator.CacheKey(Target, SearchProtocol.Newznab), CapsWith(2000));
        await store.SaveAsync(CapsAggregator.CacheKey(Sibling, SearchProtocol.Torznab), CapsWith(5030));
        await store.SaveAsync(CapsAggregator.CacheKey(Sibling, SearchProtocol.Newznab), CapsWith(2040));

        // Non-vacuity: all four entries really are stored, so the absence assertions below run
        // against rows that existed rather than against a store that never wrote anything.
        Assert.NotNull(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Target, SearchProtocol.Torznab)));
        Assert.NotNull(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Target, SearchProtocol.Newznab)));
        Assert.NotNull(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Sibling, SearchProtocol.Torznab)));
        Assert.NotNull(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Sibling, SearchProtocol.Newznab)));

        await store.DeleteAsync(Target);

        // Asserted per key, not "some entry for the target is gone": a delete that removed only the
        // Torznab row would leave the Newznab row to be adopted by the next source of that name —
        // and the stale half is the one a later fallback serves (#99).
        Assert.Null(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Target, SearchProtocol.Torznab)));
        Assert.Null(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Target, SearchProtocol.Newznab)));

        Assert.NotNull(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Sibling, SearchProtocol.Torznab)));
        Assert.NotNull(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey(Sibling, SearchProtocol.Newznab)));
    }

    /// <summary>
    /// Deleting a source that never had an entry is a no-op, not an error. The update handler
    /// deletes the old name's entries on every rename without first establishing that a refresh had
    /// ever succeeded under that name, so this is the ordinary case rather than an edge one.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_ForASourceWithNoStoredEntries_IsANoOp()
    {
        await using var context = CreateContext();
        var store = new CapsCacheStore(context);

        await store.SaveAsync(CapsAggregator.CacheKey("Kept", SearchProtocol.Torznab), CapsWith(5000));

        await store.DeleteAsync("Never stored");

        Assert.NotNull(await store.GetLastKnownGoodAsync(CapsAggregator.CacheKey("Kept", SearchProtocol.Torznab)));
    }
}
