namespace Arbitarr.Data.Maintenance;

/// <summary>
/// Outcome of a single maintenance job run: how many rows were pruned from each accumulating
/// table, and whether an incremental vacuum was executed. Returned so callers (tests, health
/// panel, logs) can observe what happened without re-querying the database.
/// </summary>
/// <param name="SearchResultCacheRowsPruned">
/// Rows removed from the search-result cache. Predicate is strictly <c>age &gt; serve_until</c>
/// (plan lines ~1058-1080) — see <see cref="Arbitarr.Core.Settings.PrunePredicates.IsSearchResultCacheEntryPrunable"/>.
/// </param>
/// <param name="MetadataCacheRowsPruned">Rows removed from the metadata/identity cache.</param>
/// <param name="SuppressionAuditLogRowsPruned">Rows removed from the suppression audit log.</param>
/// <param name="AiVerdictCacheRowsPruned">
/// Rows removed from the AI verdict cache, combining the TTL predicate
/// (<see cref="Arbitarr.Core.Settings.PrunePredicates.IsAiVerdictCacheEntryPrunable"/>) with the
/// row-ceiling LRU trim (M5 security review, MED).
/// </param>
/// <param name="EventRowsPruned">
/// Rows removed from the shared event store (#55), totalled across every kind. Retention there is
/// per-KIND and lives in <see cref="Arbitarr.Data.Events.EventRetentionPolicy"/> (decisions are kept
/// far longer than operational events), so unlike the four counts above this total does not
/// correspond to one age threshold — see <c>MaintenanceJob.PruneEventsAsync</c>.
/// </param>
/// <param name="VacuumRan">True if <c>PRAGMA incremental_vacuum</c> was executed this run.</param>
///
/// <remarks>
/// THIS RECORD COVERS <c>arbitarr.db</c> ONLY, and the two things it does not cover are deliberate
/// rather than missing. There are TWO SQLite files under the config directory; the separate
/// application-log store (<see cref="Arbitarr.Data.Logging.LogStore.DatabaseFileName"/>) is trimmed
/// on the same cadence by <c>MaintenanceHostedService</c>, and #56's automatic configuration backup
/// runs there too. Neither reports here, because neither happens inside this job: this type is
/// produced by <c>MaintenanceJob</c>, which owns one DbContext over one database and cannot observe
/// work done beside it.
///
/// <para>Backup provenance — including a FAILED pass, which is the case a caller actually needs —
/// lives in <c>BackupStateStore</c> and is served by <c>GET /api/admin/backup/status</c>. Adding
/// backup fields here instead was tried and removed: this record is built at
/// <c>MaintenanceJob.RunAsync</c>'''s single return, where the backup result is not in scope, so the
/// fields could only ever have carried their defaults. Anything spanning the stores belongs in the
/// hosted service or the state store, and should grep for <c>DatabaseFileName</c> rather than for
/// "arbitarr.db".</para>
/// </remarks>
public sealed record MaintenanceJobResult(
    int SearchResultCacheRowsPruned,
    int MetadataCacheRowsPruned,
    int SuppressionAuditLogRowsPruned,
    int AiVerdictCacheRowsPruned,
    int EventRowsPruned,
    int ExpiredSessionRowsPruned,
    bool VacuumRan);
