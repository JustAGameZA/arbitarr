using Arbitarr.Core.Settings;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves the search-result cache prune predicate is exactly <c>age &gt; serve_until</c> and
/// nothing else (plan lines ~1058-1080) — in particular that a row still within serve_until is
/// NOT prunable regardless of how it compares to fresh_until, and that pruning and serve_until
/// expiry are evaluated independently of each other.
/// </summary>
public class PrunePredicatesTests
{
    [Fact]
    public void SearchResultCache_NotPrunable_WhenWithinServeUntil()
    {
        var serveUntil = TimeSpan.FromDays(7);
        var age = TimeSpan.FromDays(6); // old relative to a 15m fresh_until, but still valid data
        Assert.False(PrunePredicates.IsSearchResultCacheEntryPrunable(age, serveUntil));
    }

    [Fact]
    public void SearchResultCache_NotPrunable_ExactlyAtServeUntil()
    {
        var serveUntil = TimeSpan.FromDays(7);
        Assert.False(PrunePredicates.IsSearchResultCacheEntryPrunable(serveUntil, serveUntil));
    }

    [Fact]
    public void SearchResultCache_Prunable_JustPastServeUntil()
    {
        var serveUntil = TimeSpan.FromDays(7);
        var age = serveUntil + TimeSpan.FromSeconds(1);
        Assert.True(PrunePredicates.IsSearchResultCacheEntryPrunable(age, serveUntil));
    }

    [Fact]
    public void SearchResultCache_NotPrunable_WhenPastFreshUntilButWithinServeUntil()
    {
        // Anti-conflation guard: an entry far past fresh_until (long since stopped being served
        // from the healthy-path cache) must still not be prunable while inside serve_until.
        var freshUntil = TimeSpan.FromMinutes(15);
        var serveUntil = TimeSpan.FromDays(7);
        var age = freshUntil + TimeSpan.FromDays(1); // way past fresh_until, still within serve_until
        Assert.False(PrunePredicates.IsSearchResultCacheEntryPrunable(age, serveUntil));
    }

    [Fact]
    public void AiVerdictCache_NotPrunable_WithinTtl()
    {
        var ttl = TimeSpan.FromDays(30);
        Assert.False(PrunePredicates.IsAiVerdictCacheEntryPrunable(TimeSpan.FromDays(29), ttl));
    }

    [Fact]
    public void AiVerdictCache_Prunable_PastTtl()
    {
        var ttl = TimeSpan.FromDays(30);
        Assert.True(PrunePredicates.IsAiVerdictCacheEntryPrunable(ttl + TimeSpan.FromSeconds(1), ttl));
    }

    [Fact]
    public void MetadataCache_PositiveEntry_UsesRefreshCadenceNotNegativeTtl()
    {
        var refreshCadence = TimeSpan.FromDays(7);
        var negativeTtl = TimeSpan.FromDays(30);
        var age = TimeSpan.FromDays(8); // past cadence, well within negative TTL

        Assert.True(PrunePredicates.IsMetadataCacheEntryPrunable(age, isNegative: false, refreshCadence, negativeTtl));
    }

    [Fact]
    public void MetadataCache_NegativeEntry_UsesNegativeTtlNotRefreshCadence()
    {
        var refreshCadence = TimeSpan.FromDays(7);
        var negativeTtl = TimeSpan.FromDays(30);
        var age = TimeSpan.FromDays(8); // past cadence, but negative entries use the longer negative TTL

        Assert.False(PrunePredicates.IsMetadataCacheEntryPrunable(age, isNegative: true, refreshCadence, negativeTtl));
    }

    [Fact]
    public void SuppressionAudit_NotPrunable_WithinRetention()
    {
        var retention = TimeSpan.FromDays(30);
        Assert.False(PrunePredicates.IsSuppressionAuditEntryPrunable(TimeSpan.FromDays(29), retention));
    }

    [Fact]
    public void SuppressionAudit_Prunable_PastRetention()
    {
        var retention = TimeSpan.FromDays(30);
        Assert.True(PrunePredicates.IsSuppressionAuditEntryPrunable(retention + TimeSpan.FromSeconds(1), retention));
    }

    /// <summary>
    /// arb-tps: a row one tick BEFORE its expiry still resolves, so it must not be pruned. The
    /// three tests around this boundary are what pin the predicate to
    /// <c>ReleaseLookupStore.FindAsync</c>: the two must agree exactly on which side of
    /// <c>ExpiresAt</c> a row is alive, or there is an instant where a link resolves but has been
    /// deleted, or is kept but no longer resolves.
    /// </summary>
    [Fact]
    public void ReleaseLookup_NotPrunable_OneTickBeforeExpiry()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.False(PrunePredicates.IsReleaseLookupEntryPrunable(now + TimeSpan.FromTicks(1), now));
    }

    /// <summary>
    /// arb-tps: the boundary is INCLUSIVE — a row exactly AT its expiry is prunable, matching
    /// <c>ReleaseLookupStore.FindAsync</c>'s <c>ExpiresAt &lt;= now</c>, which stops resolving it at
    /// that same instant.
    /// </summary>
    [Fact]
    public void ReleaseLookup_Prunable_ExactlyAtExpiry()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(PrunePredicates.IsReleaseLookupEntryPrunable(now, now));
    }

    /// <summary>arb-tps: and comfortably past it, for the ordinary case.</summary>
    [Fact]
    public void ReleaseLookup_Prunable_PastExpiry()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(PrunePredicates.IsReleaseLookupEntryPrunable(now - TimeSpan.FromDays(1), now));
    }

    /// <summary>
    /// arb-dng: a snapshot one tick BEFORE its expiry still serves, so it must not be pruned. The
    /// three tests around this boundary are what pin the predicate to
    /// <c>QuerySnapshotStore.GetAsync</c>: the two must agree exactly on which side of
    /// <c>ExpiresAt</c> a row is alive, or there is an instant where a pagination token resolves but
    /// has been deleted, or is kept but no longer resolves.
    /// </summary>
    [Fact]
    public void QuerySnapshotCache_NotPrunable_OneTickBeforeExpiry()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.False(PrunePredicates.IsQuerySnapshotCacheEntryPrunable(now + TimeSpan.FromTicks(1), now));
    }

    /// <summary>
    /// arb-dng: the boundary is INCLUSIVE — a row exactly AT its expiry is prunable, matching
    /// <c>QuerySnapshotStore.GetAsync</c>'s <c>ExpiresAt &lt;= asOf</c>, which stops serving it at
    /// that same instant.
    /// </summary>
    [Fact]
    public void QuerySnapshotCache_Prunable_ExactlyAtExpiry()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(PrunePredicates.IsQuerySnapshotCacheEntryPrunable(now, now));
    }

    /// <summary>arb-dng: and comfortably past it, for the ordinary case.</summary>
    [Fact]
    public void QuerySnapshotCache_Prunable_PastExpiry()
    {
        var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(PrunePredicates.IsQuerySnapshotCacheEntryPrunable(now - TimeSpan.FromDays(1), now));
    }
}
