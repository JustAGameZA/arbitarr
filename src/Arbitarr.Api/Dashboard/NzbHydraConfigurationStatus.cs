namespace Arbitarr.Api.Dashboard;

/// <summary>
/// Whether an NZBHydra2 source is configured, with no address or API key attached. Exists so
/// <see cref="EffectiveConfigEndpoint"/> can report <see cref="EffectiveConfigResponse.NzbHydraConfigured"/>
/// without <c>Arbitarr.Api</c> referencing <c>Arbitarr.Sources.NzbHydra</c> — AC6 reserves all
/// <c>Arbitarr.Sources.*</c> references to <c>Arbitarr.Host</c>, the sole composition root. Host
/// registers this as a singleton, populated from whatever upstream-source configuration it holds.
/// </summary>
/// <param name="IsConfigured">True if an NZBHydra2 source is currently configured.</param>
public sealed record NzbHydraConfigurationStatus(bool IsConfigured);
