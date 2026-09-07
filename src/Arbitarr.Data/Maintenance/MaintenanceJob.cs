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
/// A FIFTH table joined them with #55: the shared event store. It is pruned by
/// <see cref="PruneEventsAsync"/>, which delegates to <see cref="Events.EventRepository.PruneAsync"/>
/// because its retention is per-kind and settings-independent, unlike the four above. Anything that
/// accumulates rows belongs in this list — a table that is written but never pruned is an outage
/// with a long fuse on a homelab SQLite file, which is precisely how the event store shipped
/// (retention written and tested, but with no caller) until emission went live.
///
/// #58 ADDED A SIXTH TABLE, ApiKeys, AND IT IS DELIBERATELY NOT PRUNED HERE. Recorded because the
/// rule above ("anything that accumulates rows belongs in this list") would otherwise make its
/// absence read as the oversight it is not:
///
///   - Its rows are not log data. A revoked key is a TOMBSTONE, and keeping it is load-bearing:
///     <see cref="Entities.ApiKeyEntry.RevokedAt"/> exists so a revoked key's label and last-used
///     time survive revocation (an operator investigating what broke after revoking still has an
///     answer), and so a leaked key's hash can never be silently re-minted onto a fresh row.
///     Pruning old rows would delete exactly the evidence the tombstone exists to preserve.
///   - Its growth is bounded by operator action, not by traffic. Rows appear only when a human
///     mints a key — dozens over a deployment's lifetime — whereas every table above grows per
///     request. Recording a key's use UPDATES its row rather than inserting one (see
///     <c>ThrottledApiKeyLastUsedRecorder</c>), so a busy *arr instance adds no rows at all.
///
/// The long-fuse outage this list guards against needs unbounded per-request growth to happen, and
/// this table has none. If a future change ever makes key rows machine-generated, that reasoning
/// stops holding and it belongs in this list.
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

        var eventsPruned = await PruneEventsAsync(cancellationToken).ConfigureAwait(false);

        await RunIncrementalVacuumAsync(cancellationToken).ConfigureAwait(false);

        return new MaintenanceJobResult(
            SearchResultCacheRowsPruned: searchResultCachePruned,
            MetadataCacheRowsPruned: metadataCachePruned,
            SuppressionAuditLogRowsPruned: suppressionAuditPruned,
            AiVerdictCacheRowsPruned: aiVerdictCachePruned,
            EventRowsPruned: eventsPruned,
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

    /// <summary>
    /// Prunes the shared event store (#55) against <see cref="Events.EventRetentionPolicy"/>.
    ///
    /// This is the fifth accumulating table, added when #55 turned on the writes. Until then the
    /// table existed but nothing wrote to it, so its absence here was harmless; with emission live
    /// it takes one Decision row per suppressed release plus one SearchServed row per search, which
    /// on a busy box is thousands of rows a day. Without this call the retention policy would be
    /// decorative and the SQLite file in the config bind mount would grow without bound — the
    /// slow-motion outage the plan's §2 names.
    ///
    /// Delegated to <see cref="Events.EventRepository.PruneAsync"/> rather than reimplemented here,
    /// unlike the four tables above, because the retention windows are per-KIND and
    /// <see cref="Events.EventRetentionPolicy"/> is their single named home. Restating "180 days for
    /// decisions, 7 for everything else" as a predicate in this file would be a second place for
    /// those figures to drift from, which that policy's own doc comment explicitly warns against.
    /// The windows are also deliberately NOT settings-driven, which is why this takes no argument
    /// from the snapshot the way its four siblings do.
    /// </summary>
    private async Task<int> PruneEventsAsync(CancellationToken cancellationToken)
    {
        var repository = new Events.EventRepository(_dbContext, _timeProvider);
        var prunedByKind = await repository.PruneAsync(cancellationToken).ConfigureAwait(false);

        // Flattened to a total: MaintenanceJobResult reports one count per table, and the per-kind
        // breakdown PruneAsync returns is what its own tests assert the asymmetry against.
        return prunedByKind.Values.Sum();
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
