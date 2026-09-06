namespace Arbitarr.Data.Entities;

/// <summary>
/// A configured upstream indexer/source (#53, stage 53a — persistence only). The API key for this
/// source is deliberately NOT a property here: per the plan's §3.1 design decision, secrets live as
/// write-only rows in the existing <see cref="SettingEntry"/> table (keyed by
/// <see cref="Sources.SourceRepository.ApiKeySettingName"/>), never on this entity, so a query or
/// serialization of <see cref="Source"/> can never leak a key by accident.
///
/// Stage 53a writes these rows but nothing reads them yet — env vars remain authoritative until
/// 53b adds the DB-first, env-var-fallback resolution path described in the plan's §3.2.
/// </summary>
public sealed class Source
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The source implementation this row configures, e.g. "NzbHydra". Stored as a string rather
    /// than an enum so a new source kind never requires a schema migration to add.
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>Operator-facing name shown in the UI (53d) and logs (53b); must be unique.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Base URL of the upstream source, e.g. http://192.0.2.21:5076. Must be absolute http/https.</summary>
    public required string BaseUrl { get; set; }

    /// <summary>Whether this source participates in searches. Disabling never deletes the row.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When this source was first configured.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this source was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
