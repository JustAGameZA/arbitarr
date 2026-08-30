using System.Globalization;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Settings;

/// <summary>
/// M7-5's settings write path: validates a proposed value against <see cref="SettingsValidator"/>
/// (AC24 — reject out-of-bounds, never clamp) and, only if it passes, upserts the corresponding
/// <see cref="SettingEntry"/> row. This is the write-side counterpart to
/// <c>Arbitarr.Api.Dashboard.EffectiveSettingsReader</c>, which remains read-only.
///
/// Cross-field bounds (e.g. serve_until's floor is the *current* fresh_until) are resolved by
/// loading the current snapshot from this same table before validating, so a proposed change is
/// always checked against live values rather than stale defaults.
/// </summary>
public sealed class SettingsRepository
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeSpan _arrSyncInterval;

    public SettingsRepository(ArbitarrDbContext dbContext, TimeSpan arrSyncInterval)
    {
        _dbContext = dbContext;
        _arrSyncInterval = arrSyncInterval;
    }

    /// <summary>
    /// The AC0c-measured *arr RSS sync interval this repository validates against — exposed so
    /// callers (e.g. the admin settings endpoint, AC24/M7-8) can compute the same bounds
    /// <see cref="SettingsValidator.GetBounds"/> uses without duplicating the measurement.
    /// </summary>
    public TimeSpan ArrSyncInterval => _arrSyncInterval;

    /// <summary>Loads the current effective settings snapshot, falling back to defaults for unpersisted keys.</summary>
    public async Task<SettingsSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var defaults = SettingsSnapshot.Defaults(_arrSyncInterval);
        var rows = await _dbContext.Settings.AsNoTracking().ToDictionaryAsync(
            e => e.Name,
            e => e.Value,
            StringComparer.Ordinal,
            cancellationToken);

        return defaults with
        {
            FreshUntil = ReadTimeSpan(rows, SettingKey.FreshUntil, defaults.FreshUntil),
            ServeUntil = ReadTimeSpan(rows, SettingKey.ServeUntil, defaults.ServeUntil),
            ActiveWindow = ReadTimeSpan(rows, SettingKey.ActiveWindow, defaults.ActiveWindow),
            RefreshLead = ReadTimeSpan(rows, SettingKey.RefreshLead, defaults.RefreshLead),
            WorkerCycleInterval = ReadTimeSpan(rows, SettingKey.WorkerCycleInterval, defaults.WorkerCycleInterval),
            WorkerEnabled = ReadBool(rows, SettingKey.WorkerEnabled, defaults.WorkerEnabled),
            AiVerdictCacheTtl = ReadTimeSpan(rows, SettingKey.AiVerdictCacheTtl, defaults.AiVerdictCacheTtl),
            AiVerdictCacheRowCeiling = ReadInt(rows, SettingKey.AiVerdictCacheRowCeiling, defaults.AiVerdictCacheRowCeiling),
            MetadataRefreshCadence = ReadTimeSpan(rows, SettingKey.MetadataRefreshCadence, defaults.MetadataRefreshCadence),
            MetadataNegativeTtl = ReadTimeSpan(rows, SettingKey.MetadataNegativeTtl, defaults.MetadataNegativeTtl),
            SuppressionAuditRetention = ReadTimeSpan(rows, SettingKey.SuppressionAuditRetention, defaults.SuppressionAuditRetention),
            QuerySnapshotTtl = ReadTimeSpan(rows, SettingKey.QuerySnapshotTtl, defaults.QuerySnapshotTtl),
            MaintenanceJobInterval = ReadTimeSpan(rows, SettingKey.MaintenanceJobInterval, defaults.MaintenanceJobInterval),
        };
    }

    /// <summary>AC26b: current value of the AI kill-switch (defaults to off — AI enabled — when unset).</summary>
    public async Task<bool> GetAiKillSwitchAsync(CancellationToken cancellationToken)
    {
        var row = await _dbContext.Settings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Name == SettingKey.AiKillSwitch.ToString(), cancellationToken);
        return row is not null && bool.TryParse(row.Value, out var value) && value;
    }

    /// <summary>AC14b: current value of the sync-AI-arbitration budget, defaulting per <see cref="SettingsCatalog.GetDefault"/> when unset.</summary>
    public async Task<TimeSpan> GetSyncArbitrationBudgetAsync(CancellationToken cancellationToken)
    {
        var row = await _dbContext.Settings.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Name == SettingKey.SyncArbitrationBudget.ToString(), cancellationToken);
        return row is not null && TimeSpan.TryParse(row.Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : (TimeSpan)SettingsCatalog.GetDefault(SettingKey.SyncArbitrationBudget);
    }

    /// <summary>
    /// Validates <paramref name="proposed"/> (serialized the same way the entry's <see cref="SettingCatalogEntry.IsBoolean"/>
    /// flag indicates: <c>bool.ToString()</c> or <see cref="TimeSpan.ToString()"/>/<c>int.ToString()</c>) against
    /// <see cref="SettingsValidator"/>, then upserts it. Throws <see cref="SettingsValidationException"/> without
    /// writing anything if the value is out of bounds.
    /// </summary>
    public async Task SetAsync(SettingKey key, string proposed, CancellationToken cancellationToken)
    {
        var current = await LoadSnapshotAsync(cancellationToken);

        switch (key)
        {
            case SettingKey.FreshUntil:
                SettingsValidator.ValidateFreshUntil(ParseTimeSpan(key, proposed), _arrSyncInterval);
                break;
            case SettingKey.ServeUntil:
                SettingsValidator.ValidateServeUntil(ParseTimeSpan(key, proposed), current.FreshUntil);
                break;
            case SettingKey.ActiveWindow:
                SettingsValidator.ValidateActiveWindow(ParseTimeSpan(key, proposed), _arrSyncInterval, current.ServeUntil);
                break;
            case SettingKey.RefreshLead:
                SettingsValidator.ValidateRefreshLead(ParseTimeSpan(key, proposed), current.FreshUntil);
                break;
            case SettingKey.WorkerCycleInterval:
                SettingsValidator.ValidateWorkerCycleInterval(ParseTimeSpan(key, proposed), current.RefreshLead);
                break;
            case SettingKey.WorkerEnabled:
                ParseBool(key, proposed);
                break;
            case SettingKey.AiVerdictCacheTtl:
                SettingsValidator.ValidateAiVerdictCacheTtl(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.AiVerdictCacheRowCeiling:
                SettingsValidator.ValidateAiVerdictCacheRowCeiling(ParseInt(key, proposed));
                break;
            case SettingKey.MetadataRefreshCadence:
                SettingsValidator.ValidateMetadataRefreshCadence(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.MetadataNegativeTtl:
                SettingsValidator.ValidateMetadataNegativeTtl(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.SuppressionAuditRetention:
                SettingsValidator.ValidateSuppressionAuditRetention(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.QuerySnapshotTtl:
                SettingsValidator.ValidateQuerySnapshotTtl(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.MaintenanceJobInterval:
                SettingsValidator.ValidateMaintenanceJobInterval(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.AiKillSwitch:
                // AC26b: a boolean escape hatch, not bound-validated (SettingsCatalog.IsBoolean).
                ParseBool(key, proposed);
                break;
            case SettingKey.SyncArbitrationBudget:
                SettingsValidator.ValidateSyncArbitrationBudget(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.ShadowMode:
            case SettingKey.TitleNormalizationEnabled:
                // D3 / AC26b toggles: booleans, not bound-validated (SettingsCatalog.IsBoolean).
                ParseBool(key, proposed);
                break;
            case SettingKey.AiConfidenceThreshold:
                SettingsValidator.ValidateAiConfidenceThreshold(ParseDouble(key, proposed));
                break;
            case SettingKey.ClassifierPollInterval:
                SettingsValidator.ValidateClassifierPollInterval(ParseTimeSpan(key, proposed));
                break;
            case SettingKey.AdminApiKey:
                throw new SettingsValidationException(key,
                    "admin_api_key is not editable through the settings write path.");
            default:
                throw new SettingsValidationException(key, $"Unknown setting key '{key}'.");
        }

        await UpsertAsync(key, proposed, cancellationToken);
    }

    private async Task UpsertAsync(SettingKey key, string value, CancellationToken cancellationToken)
    {
        var name = key.ToString();
        var existing = await _dbContext.Settings.FindAsync(new object[] { name }, cancellationToken);
        if (existing is null)
        {
            _dbContext.Settings.Add(new SettingEntry
            {
                Name = name,
                Value = value,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static TimeSpan ParseTimeSpan(SettingKey key, string raw) =>
        TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SettingsValidationException(key, $"'{raw}' is not a valid duration.");

    private static bool ParseBool(SettingKey key, string raw) =>
        bool.TryParse(raw, out var value)
            ? value
            : throw new SettingsValidationException(key, $"'{raw}' is not a valid boolean.");

    private static double ParseDouble(SettingKey key, string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SettingsValidationException(key, $"'{raw}' is not a valid number.");

    private static int ParseInt(SettingKey key, string raw) =>
        int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SettingsValidationException(key, $"'{raw}' is not a valid integer.");

    private static TimeSpan ReadTimeSpan(IReadOnlyDictionary<string, string> rows, SettingKey key, TimeSpan fallback) =>
        rows.TryGetValue(key.ToString(), out var raw) && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> rows, SettingKey key, bool fallback) =>
        rows.TryGetValue(key.ToString(), out var raw) && bool.TryParse(raw, out var value)
            ? value
            : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, string> rows, SettingKey key, int fallback) =>
        rows.TryGetValue(key.ToString(), out var raw) && int.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
