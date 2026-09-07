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
public sealed record MaintenanceJobResult(
    int SearchResultCacheRowsPruned,
    int MetadataCacheRowsPruned,
    int SuppressionAuditLogRowsPruned,
    int AiVerdictCacheRowsPruned,
    int EventRowsPruned,
    bool VacuumRan);
