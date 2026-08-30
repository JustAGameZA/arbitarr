namespace Arbitarr.Core.Security;

/// <summary>
/// Resolves an inbound Torznab/Newznab client <c>apikey</c> query parameter (the key an *arr
/// client such as Sonarr or Radarr uses to call into Arbitarr) to a named <see cref="ClientKeyContext"/>.
/// This is explicitly distinct from (a) any upstream source's own API key (e.g. NZBHydra2's,
/// which Arbitarr uses to call out) and (b) <c>SettingKey.AdminApiKey</c> (a separate, later M4/M7
/// concept) — this interface governs only the client-facing key that gates <c>/torznab/api</c>,
/// <c>/newznab/api</c>, and <c>/download/{proxyGuid}</c>.
///
/// Implementations must use a fixed-time comparison when checking the provided key against every
/// configured key, so response timing cannot be used to narrow down a valid key. Endpoints must
/// depend only on this interface, never on a concrete implementation, so the config-backed
/// implementation registered by Arbitarr.Host today can be swapped for a DB-backed
/// implementation (M4's <c>ApiKeyProfileEntry</c>) without any endpoint change.
/// </summary>
public interface IClientApiKeyResolver
{
    /// <summary>
    /// Resolves <paramref name="apikey"/> to the matching configured client key's context, or
    /// <c>null</c> if the key is missing, empty, or does not match any configured key (including
    /// when no client keys are configured at all — fail closed).
    /// </summary>
    ClientKeyContext? Resolve(string? apikey);
}
