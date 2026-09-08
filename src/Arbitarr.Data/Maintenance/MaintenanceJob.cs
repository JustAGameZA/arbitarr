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
/// #98 GAVE THE OPERATOR A REMOVE ACTION FOR AN ALREADY-REVOKED KEY, AND THAT DOES NOT CHANGE THE
/// ABOVE. The distinction this list turns on is who decides: a scheduled prune would delete
/// tombstones on a timer, taking the evidence with it and without anybody asking. Removal is a
/// deliberate second act by the operator on a key they have already revoked — the same "bounded by
/// operator action" category minting is — so it belongs on the admin surface
/// (<c>ApiKeyRepository.RemoveRevokedAsync</c>) and still not here.
///
/// #44 ADDED A SEVENTH TABLE, Sessions, AND IT IS PRUNED HERE — unlike ApiKeys above. The
/// distinction is the one this list's rule turns on: session rows are machine-generated and grow
/// per LOGIN, not per operator action, and a revoked or expired session is not a tombstone anybody
/// reads. Nothing surfaces a dead session, so keeping one preserves no evidence; it only grows the
/// file. Users are NOT pruned — an account is operator-created and its whole purpose is to persist.
/// Only the config database is touched; arbitarr-logs.db has its own retention and no session rows.
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
    /// <summary>
    /// How many multiples of the configured idle window a session must sit unused before this job
    /// DELETES it. See <see cref="PruneExpiredSessionsAsync"/>: the security boundary uses the
    /// exact window, and this looser one exists so a lengthened setting cannot find the rows it
    /// would have revived already deleted.
    /// </summary>
    private const int IdleGraceFactor = 4;

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

        var expiredSessionsPruned = await PruneExpiredSessionsAsync(
                now, settings.SessionIdleTimeout, cancellationToken)
            .ConfigureAwait(false);

        await RunIncrementalVacuumAsync(cancellationToken).ConfigureAwait(false);

        return new MaintenanceJobResult(
            SearchResultCacheRowsPruned: searchResultCachePruned,
            MetadataCacheRowsPruned: metadataCachePruned,
            SuppressionAuditLogRowsPruned: suppressionAuditPruned,
            AiVerdictCacheRowsPruned: aiVerdictCachePruned,
            EventRowsPruned: eventsPruned,
            ExpiredSessionRowsPruned: expiredSessionsPruned,
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

    /// <summary>
    /// Drops session rows that can never authenticate again (#44) — past their absolute expiry, or
    /// idle beyond the configured window.
    ///
    /// <para><b>THIS IS STORAGE HYGIENE, NEVER THE SECURITY BOUNDARY.</b> A session stops
    /// authenticating the instant it is revoked or expires, because
    /// <see cref="Security.SessionRepository.FindLiveByPresentedTokenAsync"/> evaluates both
    /// expiries on every lookup rather than trusting a flag or waiting for a sweep. So this job
    /// running late, or not at all, cannot let a dead session back in — which is exactly why it is
    /// safe to prune on a timer instead of on the request path.</para>
    ///
    /// <para><b>THE TWO PREDICATES ARE NOT SYMMETRIC, AND THE IDLE ONE IS DELIBERATELY
    /// CONSERVATIVE.</b> Absolute expiry is a property of the row, fixed at issue and never
    /// extended, so a row past it is dead under every possible configuration — it is deleted
    /// outright. Idle expiry is NOT a property of the row: it is measured against the CURRENT
    /// <c>session_idle_timeout</c>, so a row that looks idle-dead today comes back to life if the
    /// operator lengthens that setting. Deleting on the live setting alone would therefore destroy
    /// sessions that a settings change would have revived, and the operator would be signed out by
    /// a maintenance pass they did not connect to the change they made.
    ///
    /// The idle arm is applied against <see cref="Entities.SessionEntry.LastSeenAt"/> plus a
    /// <see cref="IdleGraceFactor"/>x margin on the configured window, so a row is removed only
    /// once it is idle far past any plausible re-lengthening. The exact figure is not load-bearing:
    /// anything that keeps the delete well clear of the live boundary preserves the property. What
    /// matters is that the boundary the SECURITY decision uses is the exact one in
    /// <c>FindLiveByPresentedTokenAsync</c>, and the boundary this DELETE uses is looser.</para>
    /// </summary>
    private async Task<int> PruneExpiredSessionsAsync(
        DateTimeOffset now, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        // Client-side for the same reason as every prune above: SQLite's EF Core provider cannot
        // reliably translate DateTimeOffset comparisons server-side.
        var candidates = await _dbContext.Sessions
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Multiplied rather than added so the margin scales with the operator's own window: a
        // 5-minute idle timeout and a 30-day one should not share one fixed grace period.
        var idleDeleteAfter = idleTimeout * IdleGraceFactor;

        var prunable = candidates
            .Where(s => s.AbsoluteExpiresAt <= now || s.LastSeenAt.Add(idleDeleteAfter) <= now)
            .ToList();

        _dbContext.Sessions.RemoveRange(prunable);
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
