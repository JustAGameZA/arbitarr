using Arbitarr.Core.Settings;

namespace Arbitarr.Api.Dashboard;

/// <summary>
/// The effective-configuration shape served by <c>/api/config/effective</c>. Every field here is
/// an explicit allow-list entry (M2 §4): a setting not represented by a property on this record is
/// never emitted, masked or otherwise, so a future settings key cannot leak by default. In
/// particular this record intentionally carries no source URLs, host names, or API keys — even a
/// masked placeholder for those would still reveal that an upstream is configured at a specific
/// address, so this projection omits them entirely rather than trying to redact them field-by-field.
/// </summary>
/// <param name="NzbHydraConfigured">
/// Whether AT LEAST ONE ENABLED SOURCE OF ANY KIND carries an API key, with no address or key
/// disclosed. arb-72mf widened this from "an NZBHydra2 source is configured", which reported false
/// for an install whose only sources are direct Newznab/Torznab rows (#344) even though it searched
/// them correctly. The FIELD NAME is deliberately unchanged: it is public API on
/// <c>/api/config/effective</c>, so renaming it is a separate, breaking change.
/// </param>
/// <param name="FreshUntilSeconds">Search-result cache "served directly" age, in seconds.</param>
/// <param name="ServeUntilSeconds">Search-result cache outer availability-fallback age, in seconds.</param>
/// <param name="ActiveWindowSeconds">Worker "actively being requested" trailing window, in seconds.</param>
/// <param name="RefreshLeadSeconds">Worker: how far ahead of FreshUntil to refresh, in seconds.</param>
/// <param name="WorkerCycleIntervalSeconds">Worker scan period, in seconds.</param>
/// <param name="WorkerEnabled">Global on/off for proactive refresh.</param>
/// <param name="QuerySnapshotTtlSeconds">Pagination snapshot TTL, in seconds.</param>
/// <param name="ShadowMode">Whether AI/rule suppression is currently shadow-mode (annotate-but-never-suppress). Mirrors what the filter pipeline (<c>FilterStage</c>) actually enforces.</param>
public sealed record EffectiveConfigResponse(
    bool NzbHydraConfigured,
    double FreshUntilSeconds,
    double ServeUntilSeconds,
    double ActiveWindowSeconds,
    double RefreshLeadSeconds,
    double WorkerCycleIntervalSeconds,
    bool WorkerEnabled,
    double QuerySnapshotTtlSeconds,
    bool? ShadowMode);

/// <summary>Builds the allow-listed <see cref="EffectiveConfigResponse"/> from live settings.</summary>
public static class ConfigProjection
{
    public static EffectiveConfigResponse Project(SettingsSnapshot settings, bool nzbHydraConfigured, bool? shadowMode) =>
        new(
            NzbHydraConfigured: nzbHydraConfigured,
            FreshUntilSeconds: settings.FreshUntil.TotalSeconds,
            ServeUntilSeconds: settings.ServeUntil.TotalSeconds,
            ActiveWindowSeconds: settings.ActiveWindow.TotalSeconds,
            RefreshLeadSeconds: settings.RefreshLead.TotalSeconds,
            WorkerCycleIntervalSeconds: settings.WorkerCycleInterval.TotalSeconds,
            WorkerEnabled: settings.WorkerEnabled,
            QuerySnapshotTtlSeconds: settings.QuerySnapshotTtl.TotalSeconds,
            ShadowMode: shadowMode);
}
