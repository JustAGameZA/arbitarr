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
    public Task<bool> GetShadowModeAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<bool>(SettingKey.ShadowMode, bool.TryParse, cancellationToken);

    /// <summary>Resolves <see cref="SettingKey.AiConfidenceThreshold"/>, defaulting to 0.9 (D3) when no row exists.</summary>
    public Task<double> GetAiConfidenceThresholdAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<double>(
            SettingKey.AiConfidenceThreshold,
            static (string raw, out double value) => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            cancellationToken);

    /// <summary>Resolves <see cref="SettingKey.TitleNormalizationEnabled"/>, defaulting to OFF (AC26b) when no row exists.</summary>
    public Task<bool> GetTitleNormalizationEnabledAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<bool>(SettingKey.TitleNormalizationEnabled, bool.TryParse, cancellationToken);

    /// <summary>Resolves <see cref="SettingKey.ClassifierPollInterval"/>, defaulting to 1 minute when no row exists.</summary>
    public Task<TimeSpan> GetClassifierPollIntervalAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<TimeSpan>(SettingKey.ClassifierPollInterval, ParseTimeSpan, cancellationToken);

    /// <summary>
    /// AC14b: resolves <see cref="SettingKey.SyncArbitrationBudget"/>, defaulting to 5s when no row
    /// exists. Consumed by the ad-hoc search endpoint to bound its synchronous-AI opt-in.
    /// </summary>
    public Task<TimeSpan> GetSyncArbitrationBudgetAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<TimeSpan>(SettingKey.SyncArbitrationBudget, ParseTimeSpan, cancellationToken);

    private delegate bool TryParse<T>(string raw, out T value);

    private static bool ParseTimeSpan(string raw, out TimeSpan value) =>
        TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Single read path for every key: a missing row and an unparseable stored value both resolve to
    /// <see cref="SettingsCatalog.GetDefault"/>, so a bad row can never fault the live filtering path.
    /// </summary>
    private async Task<T> ReadAsync<T>(SettingKey key, TryParse<T> tryParse, CancellationToken cancellationToken)
    {
        var raw = await GetRawAsync(key, cancellationToken).ConfigureAwait(false);

        return raw is not null && tryParse(raw, out var value) ? value : (T)SettingsCatalog.GetDefault(key);
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
