using Arbitarr.Core.Filtering;
using Arbitarr.Data.Entities;

namespace Arbitarr.Data.Filtering;

/// <summary>
/// Maps Core's in-memory <see cref="SuppressionRecord"/>/<see cref="ShadowTaggedSuppression"/>
/// (produced by <see cref="RuleEngine"/>/<see cref="ShadowModeGate"/>/<see cref="SuppressionPrecedenceChain"/>)
/// to the persisted <see cref="SuppressionAuditLogEntry"/> row shape (M4-5/P3). Lives in
/// Arbitarr.Data rather than Arbitarr.Core because Core has zero references to other Arbitarr.*
/// projects (AC6); this mapping is the repository-boundary translation the doc comments on
/// <see cref="SuppressionPrecedenceChain"/> and <see cref="ShadowModeGate"/> point callers at.
/// </summary>
public static class SuppressionAuditLogMapper
{
    /// <summary>
    /// Maps a single <see cref="SuppressionRecord"/> to a <see cref="SuppressionAuditLogEntry"/>.
    /// <paramref name="ruleName"/> is the specific rule/layer identifier the record's <c>Reason</c>
    /// text is attributed to (P3: "naming the layer and the specific rule id"); pass a stable
    /// non-rule label (e.g. "ai", "pass") for suppression sources with no named rule.
    /// </summary>
    public static SuppressionAuditLogEntry ToEntry(
        SuppressionRecord record,
        string queryKey,
        string ruleName,
        bool shadowMode)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(queryKey);
        ArgumentNullException.ThrowIfNull(ruleName);

        return new SuppressionAuditLogEntry
        {
            OccurredAt = record.SuppressedAt,
            ReleaseIdentifier = record.Release.Guid,
            QueryKey = queryKey,
            RuleName = ruleName,
            Reason = record.Reason,
            ShadowMode = shadowMode,
        };
    }

    /// <summary>
    /// Maps a <see cref="ShadowTaggedSuppression"/> (already carrying its own shadow-mode flag) to
    /// a <see cref="SuppressionAuditLogEntry"/>.
    /// </summary>
    public static SuppressionAuditLogEntry ToEntry(
        ShadowTaggedSuppression tagged,
        string queryKey,
        string ruleName)
    {
        ArgumentNullException.ThrowIfNull(tagged);

        return ToEntry(tagged.Record, queryKey, ruleName, tagged.ShadowMode);
    }

    /// <summary>
    /// Maps every suppression in <paramref name="result"/> to a <see cref="SuppressionAuditLogEntry"/>,
    /// one row per suppression (M4-5: "zero suppressions occur without a record", asserted by count
    /// equality). <paramref name="ruleNameSelector"/> extracts the specific rule/layer identifier
    /// from each record's reason text; callers with a fixed rule/layer name for the whole batch may
    /// pass a constant-returning selector.
    /// </summary>
    public static IReadOnlyList<SuppressionAuditLogEntry> ToEntries(
        RuleEngineResult result,
        string queryKey,
        bool shadowMode,
        Func<SuppressionRecord, string> ruleNameSelector)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ruleNameSelector);

        return result.Suppressions
            .Select(s => ToEntry(s, queryKey, ruleNameSelector(s), shadowMode))
            .ToList();
    }
}
