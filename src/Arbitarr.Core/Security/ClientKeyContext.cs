namespace Arbitarr.Core.Security;

/// <summary>
/// Identifies a successfully resolved client apikey (the key an *arr client such as Sonarr or
/// Radarr uses to call into Arbitarr). Carries the key's <see cref="Name"/> (not just a boolean
/// valid/invalid) so per-client attribution — events, logs, rate limits, and M4's
/// <c>ApiKeyProfileEntry</c> (named key -> filter profile) association — has something to key off.
/// A single-key environment configuration collapses to one named key, <c>"default"</c>.
///
/// <para><b>#97: ONE SHAPE FOR BOTH SOURCES.</b> A key minted in Settings > API keys resolves to
/// this same record, carrying its label as the <see cref="Name"/>, so everything downstream that
/// attributes work to a client behaves identically whether the caller presented an environment key
/// or a minted one. That is the whole point of the name living here rather than at the call sites.</para>
/// </summary>
/// <param name="Name">The resolved key's name: its configured name, or a minted key's label.</param>
/// <param name="KeyId">
/// The minted key's database id, or <c>null</c> for an environment-configured key, which has no row.
/// Non-null is precisely the condition under which a last-used time can be stamped — see
/// <see cref="IApiKeyLastUsedRecorder"/>. The null is the honest representation of an environment
/// key's missing attribution rather than a synthetic row pretending Arbitarr minted it, matching
/// how <c>DbAdminKeyResolver</c> reports the legacy admin key.
/// </param>
public sealed record ClientKeyContext(string Name, long? KeyId = null);
