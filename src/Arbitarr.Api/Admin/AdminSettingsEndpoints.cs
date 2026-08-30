using Arbitarr.Core.Settings;
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
public sealed record SettingCatalogEntryResponse(
    string Key,
    string Group,
    string DisplayName,
    string Rationale,
    bool RequiresRestart,
    bool IsBoolean,
    string Value);

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
        CancellationToken cancellationToken)
    {
        var snapshot = await repository.LoadSnapshotAsync(cancellationToken);
        var aiKillSwitch = await repository.GetAiKillSwitchAsync(cancellationToken);

        var entries = SettingsCatalog.Entries.Select(entry => new SettingCatalogEntryResponse(
            Key: entry.Key.ToString(),
            Group: entry.Group.ToString(),
            DisplayName: entry.DisplayName,
            Rationale: entry.Rationale,
            RequiresRestart: entry.RequiresRestart,
            IsBoolean: entry.IsBoolean,
            Value: CurrentValue(entry.Key, snapshot, aiKillSwitch)));

        return Results.Ok(entries);
    }

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

    private static string CurrentValue(SettingKey key, SettingsSnapshot snapshot, bool aiKillSwitch) => key switch
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
        SettingKey.AiKillSwitch => aiKillSwitch.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No wire projection for this setting key."),
    };
}
