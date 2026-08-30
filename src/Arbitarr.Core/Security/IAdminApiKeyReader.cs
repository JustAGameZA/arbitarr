namespace Arbitarr.Core.Security;

/// <summary>
/// Reads the current <c>SettingKey.AdminApiKey</c> value (D2, wired at M7). Distinct from
/// <see cref="IClientApiKeyResolver"/>, which resolves the Torznab/Newznab client-facing apikey —
/// this governs only the key required on <c>RouteClassification.AdminMutating</c> routes.
///
/// The admin key is a persisted setting (not static configuration) so it can be changed at
/// runtime from the admin UI itself; implementations read it from the settings store rather than
/// from <c>IConfiguration</c>.
/// </summary>
public interface IAdminApiKeyReader
{
    /// <summary>
    /// Returns the currently configured admin API key, or <c>null</c>/empty if none has been set
    /// yet (fresh install) — in which case every admin-mutating route must fail closed (reject all
    /// requests) rather than treat "no key configured" as "no gate needed".
    /// </summary>
    Task<string?> GetCurrentKeyAsync(CancellationToken cancellationToken);
}
