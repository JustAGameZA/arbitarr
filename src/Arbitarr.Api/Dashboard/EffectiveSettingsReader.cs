using System.Globalization;
using Arbitarr.Core.Settings;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Api.Dashboard;

/// <summary>
/// Read-only projection of the persisted <see cref="SettingEntry"/> rows into a
/// <see cref="SettingsSnapshot"/>, for the dashboard's <c>/api/config/effective</c> endpoint only.
///
/// This is deliberately NOT the settings write/validation repository referenced by
/// <see cref="Arbitarr.Core.Settings.SettingsValidator"/>'s doc comments (that is worker-3's
/// scope, to be added alongside the write path). M2 only needs to display the current effective
/// values, so this type does the minimum: read whatever rows exist, and fall back to
/// <see cref="SettingsSnapshot.Defaults"/> for any key not yet persisted (e.g. before worker-3's
/// settings-write path has ever run in a fresh install).
/// </summary>
public sealed class EffectiveSettingsReader
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeSpan _arrSyncInterval;

    public EffectiveSettingsReader(ArbitarrDbContext dbContext, TimeSpan arrSyncInterval)
    {
        _dbContext = dbContext;
        _arrSyncInterval = arrSyncInterval;
    }

    public async Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken)
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
            QuerySnapshotTtl = ReadTimeSpan(rows, SettingKey.QuerySnapshotTtl, defaults.QuerySnapshotTtl),
        };
    }

    private static TimeSpan ReadTimeSpan(IReadOnlyDictionary<string, string> rows, SettingKey key, TimeSpan fallback) =>
        rows.TryGetValue(key.ToString(), out var raw) && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, string> rows, SettingKey key, bool fallback) =>
        rows.TryGetValue(key.ToString(), out var raw) && bool.TryParse(raw, out var value)
            ? value
            : fallback;
}
