using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Maintenance;

/// <summary>
/// Prunes the accumulating cache-like tables and reclaims freed pages, per the plan's retention
/// policy (plan lines ~1027-1032, ~1058-1080). This is the DB-touching implementation; the
/// prune-eligibility predicates themselves live in <see cref="Arbitarr.Core.Settings.PrunePredicates"/>
/// (reference-free of EF Core) so they are independently unit-testable and so this class stays a
/// thin adapter over them.
///
/// Note on scope: the plan's retention table names four accumulating tables — search-result
/// cache, AI verdict cache, metadata cache, and suppression audit log. All four now have a
/// persisted schema (see <see cref="ArbitarrDbContext"/>); the AI verdict cache prune
/// (<see cref="PruneAiVerdictCacheAsync"/>) combines the age/TTL predicate in
/// <see cref="Arbitarr.Core.Settings.PrunePredicates.IsAiVerdictCacheEntryPrunable"/> with a
/// separate row-ceiling LRU trim (M5 security review, MED) that this job applies directly.
///
/// Scheduling: run on an interval equal to the <c>maintenance_job_interval</c> setting. Per
/// <see cref="SettingsValidator.ValidateMaintenanceJobInterval"/>, this is the one setting
/// explicitly permitted to require a restart to take effect — callers that own a recurring timer
/// (e.g. a hosted service in Arbitarr.Host) must read the interval once at startup/at each
/// fire and are not required to react to a live change without a restart, unlike every other
/// setting in the catalog.
/// </summary>
public sealed class MaintenanceJob
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public MaintenanceJob(ArbitarrDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs one maintenance pass: prunes each accumulating table against the current settings
    /// snapshot, then runs an incremental vacuum to actually return freed pages to the OS.
    /// </summary>
    public async Task<MaintenanceJobResult> RunAsync(SettingsSnapshot settings, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var searchResultCachePruned = await PruneSearchResultCacheAsync(now, settings.ServeUntil, cancellationToken)
            .ConfigureAwait(false);

        var metadataCachePruned = await PruneMetadataCacheAsync(
                now, settings.MetadataRefreshCadence, settings.MetadataNegativeTtl, cancellationToken)
            .ConfigureAwait(false);

        var suppressionAuditPruned = await PruneSuppressionAuditLogAsync(
                now, settings.SuppressionAuditRetention, cancellationToken)
            .ConfigureAwait(false);

        var aiVerdictCachePruned = await PruneAiVerdictCacheAsync(
                now, settings.AiVerdictCacheTtl, settings.AiVerdictCacheRowCeiling, cancellationToken)
            .ConfigureAwait(false);

        await RunIncrementalVacuumAsync(cancellationToken).ConfigureAwait(false);

        return new MaintenanceJobResult(
            SearchResultCacheRowsPruned: searchResultCachePruned,
            MetadataCacheRowsPruned: metadataCachePruned,
            SuppressionAuditLogRowsPruned: suppressionAuditPruned,
            AiVerdictCacheRowsPruned: aiVerdictCachePruned,
            VacuumRan: true);
    }

    private async Task<int> PruneSearchResultCacheAsync(DateTimeOffset now, TimeSpan serveUntil, CancellationToken cancellationToken)
    {
        // Predicate MUST be exactly age > serve_until (never a fixed wall-clock age, never a
        // multiple of fresh_until, never LRU). SQLite's EF Core provider cannot reliably translate
        // DateTimeOffset arithmetic/comparisons server-side, so candidates are evaluated
        // client-side directly against PrunePredicates.IsSearchResultCacheEntryPrunable — the
        // single source of truth for this predicate — rather than re-expressing the same rule as
        // a (potentially divergent) SQL WHERE clause. A row's own persisted ServeUntil timestamp
        // (set at fetch time from the *then-current* serve_until setting) is intentionally NOT
        // used here: pruning must reflect the *current* setting, so a lowered serve_until takes
        // effect on already-cached rows without requiring them to be re-fetched first.
        var candidates = await _dbContext.SearchResultCacheEntries
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var prunable = candidates
            .Where(e => PrunePredicates.IsSearchResultCacheEntryPrunable(now - e.FetchedAt, serveUntil))
            .ToList();

        _dbContext.SearchResultCacheEntries.RemoveRange(prunable);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return prunable.Count;
    }

    private async Task<int> PruneMetadataCacheAsync(
        DateTimeOffset now, TimeSpan refreshCadence, TimeSpan negativeTtl, CancellationToken cancellationToken)
    {
        // See PruneSearchResultCacheAsync: SQLite's provider cannot reliably translate
        // DateTimeOffset comparisons server-side, so filtering is done client-side against
        // PrunePredicates.IsMetadataCacheEntryPrunable directly.
        var candidates = await _dbContext.MetadataCacheEntries
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var prunable = candidates
            .Where(e => PrunePredicates.IsMetadataCacheEntryPrunable(now - e.FetchedAt, e.IsNegative, refreshCadence, negativeTtl))
            .ToList();

        _dbContext.MetadataCacheEntries.RemoveRange(prunable);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return prunable.Count;
    }

    private async Task<int> PruneSuppressionAuditLogAsync(DateTimeOffset now, TimeSpan retention, CancellationToken cancellationToken)
    {
        // See PruneSearchResultCacheAsync: SQLite's provider cannot reliably translate
        // DateTimeOffset comparisons server-side, so filtering is done client-side against
        // PrunePredicates.IsSuppressionAuditEntryPrunable directly.
        var candidates = await _dbContext.SuppressionAuditLogEntries
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var prunable = candidates
            .Where(e => PrunePredicates.IsSuppressionAuditEntryPrunable(now - e.OccurredAt, retention))
            .ToList();

        _dbContext.SuppressionAuditLogEntries.RemoveRange(prunable);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return prunable.Count;
    }

    private async Task<int> PruneAiVerdictCacheAsync(
        DateTimeOffset now, TimeSpan ttl, int rowCeiling, CancellationToken cancellationToken)
    {
        // See PruneSearchResultCacheAsync: SQLite's provider cannot reliably translate
        // DateTimeOffset comparisons server-side, so TTL filtering is done client-side against
        // PrunePredicates.IsAiVerdictCacheEntryPrunable directly.
        var candidates = await _dbContext.VerdictCacheEntries
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ttlExpired = candidates
            .Where(e => PrunePredicates.IsAiVerdictCacheEntryPrunable(now - e.LastAccessedAt, ttl))
            .ToList();

        // Row-ceiling LRU trim (M5 security review, MED): a separate mechanism from the TTL
        // predicate above (see PrunePredicates.IsAiVerdictCacheEntryPrunable's doc comment) — keep
        // only the rowCeiling most-recently-accessed survivors, regardless of TTL, so an unbounded
        // stream of distinct releases cannot grow this table without limit even when accessed
        // faster than the TTL would otherwise expire them.
        var ttlExpiredIds = ttlExpired.Select(e => e.Id).ToHashSet();
        var survivors = candidates.Where(e => !ttlExpiredIds.Contains(e.Id));
        var overCeiling = survivors
            .OrderByDescending(e => e.LastAccessedAt)
            .Skip(rowCeiling < 0 ? 0 : rowCeiling)
            .ToList();

        var prunable = new List<VerdictCacheEntry>(ttlExpired.Count + overCeiling.Count);
        prunable.AddRange(ttlExpired);
        prunable.AddRange(overCeiling);

        _dbContext.VerdictCacheEntries.RemoveRange(prunable);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return prunable.Count;
    }

    private async Task RunIncrementalVacuumAsync(CancellationToken cancellationToken)
    {
        // incremental_vacuum requires auto_vacuum = INCREMENTAL to have been set when the database
        // file was created; it is a no-op (not an error) otherwise, so this is safe to call
        // unconditionally on every maintenance pass.
        await _dbContext.Database
            .ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", cancellationToken)
            .ConfigureAwait(false);
    }
}
