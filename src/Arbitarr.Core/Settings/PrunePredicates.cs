namespace Arbitarr.Core.Settings;

/// <summary>
/// Pure prune-eligibility predicates for the four accumulating tables (search-result cache, AI
/// verdict cache, metadata cache, suppression audit log — plan lines ~1027-1032). Reference-free
/// of persistence so the Data-layer maintenance job can call these without duplicating the
/// policy, and so these predicates are unit-testable without a database.
///
/// These answer "may this row be deleted from disk?" — a disk-space, scheduled-job question.
/// They are deliberately distinct from and must never be confused with "may this entry still be
/// served?", which is a read-time correctness question answered elsewhere (e.g. by comparing an
/// entry's age against serve_until directly at the point of read). See plan lines ~1058-1080 for
/// the full non-conflation rationale.
/// </summary>
public static class PrunePredicates
{
    /// <summary>
    /// Search-result cache prune predicate. MUST be exactly <c>age &gt; serveUntil</c> — never a
    /// fixed wall-clock age, never a multiple of fresh_until, never LRU. A row inside serve_until
    /// is legitimately valid data (the worker may be keeping it alive, or it is the fallback of
    /// last resort), and deleting it early is the D3 anti-pattern this predicate exists to
    /// prevent. If disk pressure requires bounding this table further, the correct remedy is
    /// lowering serve_until itself (a visible, validated setting change) — never tightening this
    /// predicate independently of it.
    /// </summary>
    public static bool IsSearchResultCacheEntryPrunable(TimeSpan age, TimeSpan serveUntil)
        => age > serveUntil;

    /// <summary>
    /// AI verdict cache TTL-eligibility predicate (age since last access, per the plan's
    /// last-access eviction policy). The row-ceiling LRU trim is a separate, independent
    /// mechanism (see <see cref="SettingsSnapshot.AiVerdictCacheRowCeiling"/>) applied by the
    /// maintenance job on top of this age-based rule, not folded into this predicate.
    /// </summary>
    public static bool IsAiVerdictCacheEntryPrunable(TimeSpan ageSinceLastAccess, TimeSpan ttl)
        => ageSinceLastAccess > ttl;

    /// <summary>
    /// Metadata/identity cache prune-eligibility predicate. Distinct from "needs refresh": a
    /// metadata entry is refreshed (re-fetched) on its cadence or on a source-snapshot-version
    /// change (AC-M8), but is only prunable outright once its TTL has fully elapsed with no
    /// successful refresh — refreshCadence for positive entries, negativeTtl for negative
    /// ("no coverage") entries.
    /// </summary>
    public static bool IsMetadataCacheEntryPrunable(TimeSpan age, bool isNegative, TimeSpan refreshCadence, TimeSpan negativeTtl)
        => age > (isNegative ? negativeTtl : refreshCadence);

    /// <summary>
    /// Suppression audit log prune-eligibility predicate: bounded window, never indefinite, but
    /// "inspectable" does not mean "forever" — prunable once older than the configured retention.
    /// </summary>
    public static bool IsSuppressionAuditEntryPrunable(TimeSpan age, TimeSpan retention)
        => age > retention;
}
