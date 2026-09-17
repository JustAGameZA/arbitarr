namespace Arbitarr.Api.Dashboard;

/// <summary>
/// Whether any upstream source is configured, with no address or API key attached. Exists so
/// <see cref="EffectiveConfigEndpoint"/> can report <see cref="EffectiveConfigResponse.NzbHydraConfigured"/>
/// without <c>Arbitarr.Api</c> referencing <c>Arbitarr.Sources.NzbHydra</c> — AC6 reserves all
/// <c>Arbitarr.Sources.*</c> references to <c>Arbitarr.Host</c>, the sole composition root. Host
/// registers this as a singleton, populated from whatever upstream-source configuration it holds.
///
/// <para><b>arb-72mf: the NZBHydra in the name is history, not the predicate.</b> This answered
/// "is an NZBHydra source configured" until an install could have only direct Newznab/Torznab rows
/// (#344), at which point it reported not-configured for an install that searched perfectly well.
/// It now answers the question the dashboard was always asking. The name is kept because
/// <c>nzbHydraConfigured</c> is public API on <c>/api/config/effective</c>; renaming it is a
/// separate, breaking change.</para>
/// </summary>
/// <param name="IsConfigured">
/// True if AT LEAST ONE ENABLED SOURCE OF ANY KIND carries an API key. Host computes this in
/// <c>SourceSeeder</c>'s resolve pass and publishes it as
/// <c>ResolvedSourceConfiguration.AnySourceConfigured</c>; it is derived from the EXISTENCE of the
/// write-only key row, never from a key's value.
/// </param>
public sealed record NzbHydraConfigurationStatus(bool IsConfigured);
