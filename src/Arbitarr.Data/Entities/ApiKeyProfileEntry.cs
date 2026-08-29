namespace Arbitarr.Data.Entities;

/// <summary>
/// Maps a named API key (identity, per A3) to the <see cref="FilterProfileEntry"/> that should
/// apply to searches authenticated with it. Distinct from <see cref="SettingKey.AdminApiKey"/>
/// (Arbitarr.Core.Settings), which gates mutating admin actions rather than selecting a search
/// filter profile.
/// </summary>
public sealed class ApiKeyProfileEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>The named API key identifying the calling *arr instance/user.</summary>
    public required string ApiKeyName { get; set; }

    /// <summary>The filter profile this API key resolves to.</summary>
    public long FilterProfileId { get; set; }

    /// <summary>When this mapping was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
