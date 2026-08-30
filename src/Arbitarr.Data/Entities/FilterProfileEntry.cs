namespace Arbitarr.Data.Entities;

/// <summary>
/// A named collection of filter rules (Step 5). Distinct API keys can map to distinct profiles
/// (A3) via <see cref="ApiKeyProfileEntry"/>; a "default" profile applies when no API key mapping
/// exists.
/// </summary>
public sealed class FilterProfileEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Unique, human-readable profile name.</summary>
    public required string Name { get; set; }

    /// <summary>Whether this is the fallback profile used when no API key mapping matches.</summary>
    public bool IsDefault { get; set; }

    /// <summary>When this profile was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this profile was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
