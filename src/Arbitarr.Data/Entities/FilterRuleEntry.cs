namespace Arbitarr.Data.Entities;

/// <summary>
/// A single persisted deterministic filter rule (Step 5). Rules are simple allow/deny pattern
/// matches evaluated by the rule engine at <c>Arbitarr.Core.Filtering</c>; this entity is the
/// schema only — evaluation semantics live in <c>Arbitarr.Core.Filtering.RuleEvaluator</c>.
/// </summary>
public sealed class FilterRuleEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>The profile this rule belongs to.</summary>
    public long FilterProfileId { get; set; }

    /// <summary>Human-readable rule name, surfaced in suppression audit records.</summary>
    public required string Name { get; set; }

    /// <summary>Whether this is an allow rule (true) or a deny rule (false).</summary>
    public bool IsAllow { get; set; }

    /// <summary>The regex pattern this rule matches against (release title, per current scope).</summary>
    public required string Pattern { get; set; }

    /// <summary>Rule-priority ordering when multiple rules of the same kind match (see Precedence).</summary>
    public int Precedence { get; set; }

    /// <summary>Whether this rule is currently active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When this rule was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this rule was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
