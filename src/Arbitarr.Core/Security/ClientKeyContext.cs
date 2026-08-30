namespace Arbitarr.Core.Security;

/// <summary>
/// Identifies a successfully resolved client apikey (the key an *arr client such as Sonarr or
/// Radarr uses to call into Arbitarr). Carries the key's configured <see cref="Name"/> (not just
/// a boolean valid/invalid) so M4's later DB-backed <c>ApiKeyProfileEntry</c> (named key -> filter
/// profile) association has something to key off. A single-key configuration collapses to one
/// named key, <c>"default"</c>.
/// </summary>
/// <param name="Name">The configured name of the resolved key.</param>
public sealed record ClientKeyContext(string Name);
