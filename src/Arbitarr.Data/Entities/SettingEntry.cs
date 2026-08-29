namespace ArrSearcher.Data.Entities;

/// <summary>
/// Generic, typed-row settings store: one row per named setting, holding its current value and
/// its validation bounds. Kept schema-generic (name/value/floor/ceiling) so worker-3's settings
/// validation logic can add or edit rows without requiring a migration. This entity is the
/// schema only — floor/ceiling enforcement and "none needed" bound semantics are owned by
/// worker-3.
/// </summary>
public sealed class SettingEntry
{
    /// <summary>Unique setting name (e.g. "search_result_cache.fresh_until_seconds").</summary>
    public required string Name { get; set; }

    /// <summary>Current effective value, serialized as a string (interpretation is caller-defined).</summary>
    public required string Value { get; set; }

    /// <summary>
    /// Serialized minimum floor for this setting's value, or <c>null</c> if no floor applies.
    /// </summary>
    public string? Floor { get; set; }

    /// <summary>
    /// Serialized maximum ceiling for this setting's value, or <c>null</c> if no ceiling applies
    /// ("none needed" per the plan's retention table — absence here is a deliberate design
    /// decision made elsewhere, not an omission).
    /// </summary>
    public string? Ceiling { get; set; }

    /// <summary>When this setting's value was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
