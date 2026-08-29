namespace Arbitarr.Data.Entities;

/// <summary>
/// Append-only log of filter/suppression decisions, so suppressions stay inspectable (P3) within
/// a bounded retention window (never indefinite; see plan's retention table). Filter-rule and
/// profile entities that produce these decisions are Step 5 — out of scope here.
/// </summary>
public sealed class SuppressionAuditLogEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>When the suppression decision was made.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Identifier of the release candidate the decision applied to.</summary>
    public required string ReleaseIdentifier { get; set; }

    /// <summary>The query context this decision occurred under.</summary>
    public required string QueryKey { get; set; }

    /// <summary>Name/identifier of the rule or filter that produced this decision.</summary>
    public required string RuleName { get; set; }

    /// <summary>Human-readable reason for the suppression decision.</summary>
    public required string Reason { get; set; }

    /// <summary>True if the decision was made in shadow mode (recorded but not enforced).</summary>
    public bool ShadowMode { get; set; }
}
