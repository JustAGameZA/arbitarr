using System.Globalization;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Maintenance;
using Arbitarr.Data.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// One catalog entry as served over the wire: the static description from
/// <see cref="SettingsCatalog"/> plus the setting's current effective value. Values are always
/// serialized as strings (durations via <see cref="TimeSpan.ToString()"/>, booleans/ints via their
/// own <c>ToString()</c>) — the same wire shape <see cref="AdminSettingsEndpoints"/>'s PUT accepts,
/// so a GET response can be edited and PUT back unchanged.
/// </summary>
/// <param name="Min">
/// The current floor for this setting (same string form as <see cref="Value"/>), from
/// <see cref="SettingsValidator.GetBounds"/>, or null for an unbounded/boolean setting (AC24/M7-8) —
/// the admin UI renders bounds from this payload rather than hardcoding them.
/// </param>
/// <param name="Max">The current ceiling for this setting, or null if there is none.</param>
/// <param name="NoMaximumReason">M7-8: why <paramref name="Max"/> is null for a non-boolean setting; null when a ceiling exists.</param>
/// <param name="RestartReason">M7-8: why the setting only takes effect after a restart; null unless <paramref name="RequiresRestart"/>.</param>
/// <param name="GovernedTable">
/// M7-8: the SQLite table whose growth this setting governs (a retention window, TTL or row ceiling),
/// or null for settings that govern no storage.
/// </param>
/// <param name="GovernedTableRows">
/// Current row count of <paramref name="GovernedTable"/> so the storage cost of the retention
/// choice is visible where it is made; null when the setting governs no table.
/// </param>
public sealed record SettingCatalogEntryResponse(
    string Key,
    string Group,
    string DisplayName,
    string Rationale,
    bool RequiresRestart,
    bool IsBoolean,
    string Value,
    string? Min,
    string? Max,
    string? NoMaximumReason,
    string? RestartReason,
    string? GovernedTable,
    long? GovernedTableRows);

/// <summary>Request body for <c>PUT /api/admin/settings/{key}</c>.</summary>
public sealed record UpdateSettingRequest(string Value);

/// <summary>
/// M7-5: admin-gated settings surface. <c>GET /api/admin/settings</c> lists the full catalog with
/// current values (admin-gated, not <see cref="Arbitarr.Api.Routing.RouteClassification.PublicRead"/>,
/// since it is part of the admin surface rather than the lite dashboard); <c>PUT
/// /api/admin/settings/{key}</c> validates and persists one setting per AC24 (reject out-of-bounds,
/// never clamp) via <see cref="SettingsRepository"/>.
/// </summary>
public static class AdminSettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/settings", GetSettingsAsync)
            .RequireAdminApiKey();

        endpoints.MapPut("/api/admin/settings/{key}", UpdateSettingAsync)
            .RequireAdminApiKey();
    }

    private static async Task<IResult> GetSettingsAsync(
        SettingsRepository repository,
        SettingsReader reader,
        DatabaseSizeReporter sizeReporter,
        CancellationToken cancellationToken)
    {
        var snapshot = await repository.LoadSnapshotAsync(cancellationToken);
        var live = new LiveValues(
            AiKillSwitch: await repository.GetAiKillSwitchAsync(cancellationToken),
            SyncArbitrationBudget: await repository.GetSyncArbitrationBudgetAsync(cancellationToken),
            ShadowMode: await reader.GetShadowModeAsync(cancellationToken),
            AiConfidenceThreshold: await reader.GetAiConfidenceThresholdAsync(cancellationToken),
            TitleNormalizationEnabled: await reader.GetTitleNormalizationEnabledAsync(cancellationToken),
            ClassifierPollInterval: await reader.GetClassifierPollIntervalAsync(cancellationToken));
        var sizes = await sizeReporter.ReadAsync(cancellationToken);

        var entries = SettingsCatalog.Entries.Select(entry =>
        {
            var (min, max) = SettingsValidator.GetBounds(snapshot, entry.Key, repository.ArrSyncInterval);
            var governedTable = GovernedTableOf(entry.Key, sizeReporter);
            return new SettingCatalogEntryResponse(
                Key: entry.Key.ToString(),
                Group: entry.Group.ToString(),
                DisplayName: entry.DisplayName,
                Rationale: entry.Rationale,
                RequiresRestart: entry.RequiresRestart,
                IsBoolean: entry.IsBoolean,
                Value: CurrentValue(entry.Key, snapshot, live),
                Min: min,
                Max: max,
                NoMaximumReason: entry.NoMaximumReason,
                RestartReason: entry.RestartReason,
                GovernedTable: governedTable,
                GovernedTableRows: governedTable is null ? null : sizes.RowsIn(governedTable));
        });

        return Results.Ok(entries);
    }

    /// <summary>
    /// M7-8: which table a storage-governing setting bounds. Search-result freshness/serve windows and
    /// the worker keep search-result rows alive; the verdict TTL and row ceiling bound the AI verdict
    /// cache; metadata cadence and negative TTL bound the metadata cache; audit retention bounds the
    /// suppression audit log; the snapshot TTL bounds pagination snapshots.
    /// </summary>
    private static string? GovernedTableOf(SettingKey key, DatabaseSizeReporter sizeReporter) => key switch
    {
        SettingKey.FreshUntil or SettingKey.ServeUntil or SettingKey.ActiveWindow or SettingKey.RefreshLead
            or SettingKey.WorkerCycleInterval or SettingKey.WorkerEnabled => sizeReporter.TableNameOf<SearchResultCacheEntry>(),
        SettingKey.AiVerdictCacheTtl or SettingKey.AiVerdictCacheRowCeiling => sizeReporter.TableNameOf<VerdictCacheEntry>(),
        SettingKey.MetadataRefreshCadence or SettingKey.MetadataNegativeTtl => sizeReporter.TableNameOf<MetadataCacheEntry>(),
        SettingKey.SuppressionAuditRetention => sizeReporter.TableNameOf<SuppressionAuditLogEntry>(),
        SettingKey.QuerySnapshotTtl => sizeReporter.TableNameOf<QuerySnapshotCacheEntry>(),
        _ => null,
    };

    private static async Task<IResult> UpdateSettingAsync(
        string key,
        UpdateSettingRequest request,
        SettingsRepository repository,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SettingKey>(key, ignoreCase: true, out var settingKey)
            || SettingsCatalog.Entries.All(e => e.Key != settingKey))
        {
            return Results.NotFound(new { error = $"Unknown or non-editable setting '{key}'." });
        }

        try
        {
            await repository.SetAsync(settingKey, request.Value, cancellationToken);
        }
        catch (SettingsValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.Ok();
    }

    /// <summary>
    /// Settings that are not part of <see cref="SettingsSnapshot"/> (which only carries the cache /
    /// worker / maintenance keys) and are read individually from their own accessors instead.
    /// </summary>
    private sealed record LiveValues(
        bool AiKillSwitch,
        TimeSpan SyncArbitrationBudget,
        bool ShadowMode,
        double AiConfidenceThreshold,
        bool TitleNormalizationEnabled,
        TimeSpan ClassifierPollInterval);

    private static string CurrentValue(SettingKey key, SettingsSnapshot snapshot, LiveValues live) => key switch
    {
        SettingKey.FreshUntil => snapshot.FreshUntil.ToString(),
        SettingKey.ServeUntil => snapshot.ServeUntil.ToString(),
        SettingKey.ActiveWindow => snapshot.ActiveWindow.ToString(),
        SettingKey.RefreshLead => snapshot.RefreshLead.ToString(),
        SettingKey.WorkerCycleInterval => snapshot.WorkerCycleInterval.ToString(),
        SettingKey.WorkerEnabled => snapshot.WorkerEnabled.ToString(),
        SettingKey.AiVerdictCacheTtl => snapshot.AiVerdictCacheTtl.ToString(),
        SettingKey.AiVerdictCacheRowCeiling => snapshot.AiVerdictCacheRowCeiling.ToString(),
        SettingKey.MetadataRefreshCadence => snapshot.MetadataRefreshCadence.ToString(),
        SettingKey.MetadataNegativeTtl => snapshot.MetadataNegativeTtl.ToString(),
        SettingKey.SuppressionAuditRetention => snapshot.SuppressionAuditRetention.ToString(),
        SettingKey.QuerySnapshotTtl => snapshot.QuerySnapshotTtl.ToString(),
        SettingKey.MaintenanceJobInterval => snapshot.MaintenanceJobInterval.ToString(),
        SettingKey.AiKillSwitch => live.AiKillSwitch.ToString(),
        SettingKey.SyncArbitrationBudget => live.SyncArbitrationBudget.ToString(),
        SettingKey.ShadowMode => live.ShadowMode.ToString(),
        SettingKey.AiConfidenceThreshold => live.AiConfidenceThreshold.ToString(CultureInfo.InvariantCulture),
        SettingKey.TitleNormalizationEnabled => live.TitleNormalizationEnabled.ToString(),
        SettingKey.ClassifierPollInterval => live.ClassifierPollInterval.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No wire projection for this setting key."),
    };
}
