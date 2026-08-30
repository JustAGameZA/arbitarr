using System.Globalization;
using Arbitarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Settings;

/// <summary>
/// Resolves settings the live filtering path needs at request time
/// (<see cref="SettingKey.ShadowMode"/>, <see cref="SettingKey.AiConfidenceThreshold"/>) from
/// <see cref="Entities.SettingEntry"/> rows, falling back to <see cref="SettingsCatalog.GetDefault"/>
/// when no row exists yet (fresh install, M4-8 not yet populated). Lives in Arbitarr.Data rather
/// than Arbitarr.Core because Core has zero references to the persistence layer (AC6); mirrors the
/// <c>CapsCacheStore</c> pattern (constructor-injected <see cref="ArbitarrDbContext"/>,
/// <c>AsNoTracking</c> reads).
/// </summary>
public sealed class SettingsReader
{
    private readonly ArbitarrDbContext _dbContext;

    public SettingsReader(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>Resolves <see cref="SettingKey.ShadowMode"/>, defaulting to ON (D3) when no row exists.</summary>
    public async Task<bool> GetShadowModeAsync(CancellationToken cancellationToken = default)
    {
        var raw = await GetRawAsync(SettingKey.ShadowMode, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return (bool)SettingsCatalog.GetDefault(SettingKey.ShadowMode);
        }

        return bool.TryParse(raw, out var value) ? value : (bool)SettingsCatalog.GetDefault(SettingKey.ShadowMode);
    }

    /// <summary>Resolves <see cref="SettingKey.AiConfidenceThreshold"/>, defaulting to 0.9 (D3) when no row exists.</summary>
    public async Task<double> GetAiConfidenceThresholdAsync(CancellationToken cancellationToken = default)
    {
        var raw = await GetRawAsync(SettingKey.AiConfidenceThreshold, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return (double)SettingsCatalog.GetDefault(SettingKey.AiConfidenceThreshold);
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : (double)SettingsCatalog.GetDefault(SettingKey.AiConfidenceThreshold);
    }

    private async Task<string?> GetRawAsync(SettingKey key, CancellationToken cancellationToken)
    {
        var entry = await _dbContext.Settings
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Name == key.ToString(), cancellationToken)
            .ConfigureAwait(false);

        return entry?.Value;
    }
}
