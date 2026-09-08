namespace Arbitarr.Core.Security;

/// <summary>
/// Resolves an inbound Torznab/Newznab client <c>apikey</c> query parameter (the key an *arr
/// client such as Sonarr or Radarr uses to call into Arbitarr) to a named <see cref="ClientKeyContext"/>.
/// This is explicitly distinct from (a) any upstream source's own API key (e.g. NZBHydra2's,
/// which Arbitarr uses to call out) and (b) <c>SettingKey.AdminApiKey</c> (a separate, later M4/M7
/// concept) — this interface governs only the client-facing key that gates <c>/torznab/api</c>,
/// <c>/newznab/api</c>, and <c>/download/{proxyGuid}</c>.
///
/// An implementation holding candidate keys IN MEMORY must compare them in fixed time, so response
/// timing cannot be used to narrow down a valid key — that is what the environment-key path does.
/// An implementation verifying against the stored key table instead delegates to
/// <c>ApiKeyRepository.FindLiveByPresentedKeyAsync</c>, whose own doc comment records why an
/// indexed equality on the SHA-256 hash is the right trade there and what it does and does not
/// leak; that reasoning is load-bearing and is not restated here. Endpoints must depend only on
/// this interface, never on a concrete implementation.
///
/// <para><b>#97: WHY THIS IS ASYNC.</b> It was synchronous while the only implementation read a
/// config array held in memory. Since #58 a client may also present a key minted from the UI, whose
/// verification is a database lookup, and the resolution has to happen per request. Two shapes were
/// available:</para>
/// <list type="bullet">
/// <item><b>Rejected — keep <c>Resolve</c> synchronous over a cached snapshot of the key table,
/// invalidated whenever a key is minted or revoked.</b> It preserves this signature and the three
/// endpoints, but it fails in the wrong direction: a missed invalidation leaves a REVOKED key still
/// opening <c>/download/{proxyGuid}</c>, and nothing in the system notices. Revocation that is only
/// eventually true is not revocation, and a cache whose staleness bug is a security bug has to be
/// right every time an unrelated future change touches a key write path.</item>
/// <item><b>Chosen — make the interface async.</b> The lookup is an index seek on the hashed key
/// column (see <c>ApiKeyRepository.FindLiveByPresentedKeyAsync</c>), all three call sites are
/// already <c>async</c> handlers, and the cost is one <c>await</c> each. Revocation takes effect on
/// the next request because there is no second copy of the truth to keep in step.</item>
/// </list>
/// </summary>
public interface IClientApiKeyResolver
{
    /// <summary>
    /// Resolves <paramref name="apikey"/> to the matching client key's context, or <c>null</c> if
    /// the key is missing, empty, revoked, or does not match any configured or minted key
    /// (including when no client keys exist at all — fail closed).
    /// </summary>
    Task<ClientKeyContext?> ResolveAsync(string? apikey, CancellationToken cancellationToken);
}
