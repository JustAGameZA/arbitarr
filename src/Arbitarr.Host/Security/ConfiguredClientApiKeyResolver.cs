using System.Security.Cryptography;
using System.Text;
using Arbitarr.Core.Security;

namespace Arbitarr.Host.Security;

/// <summary>
/// Config-backed <see cref="IClientApiKeyResolver"/>: reads a list of named client keys from
/// <c>Arbitarr:ClientApiKeys</c> (each entry has a <c>Name</c> and a <c>Key</c>). A single legacy
/// <c>Arbitarr:ApiKey</c> value (no name) collapses to one named key, <c>"default"</c>, for
/// backward compatibility with the M1-9 single-key configuration shape.
///
/// Comparison against every configured key is fixed-time (<see cref="CryptographicOperations.FixedTimeEquals"/>)
/// so response timing cannot be used to narrow down a valid key.
/// </summary>
public sealed class ConfiguredClientApiKeyResolver : IClientApiKeyResolver
{
    private readonly IReadOnlyList<(string Name, byte[] KeyBytes)> _keys;

    public ConfiguredClientApiKeyResolver(IReadOnlyList<NamedClientApiKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys
            .Where(k => !string.IsNullOrEmpty(k.Key))
            .Select(k => (k.Name, Encoding.UTF8.GetBytes(k.Key)))
            .ToArray();
    }

    public ClientKeyContext? Resolve(string? apikey)
    {
        if (string.IsNullOrEmpty(apikey) || _keys.Count == 0)
        {
            return null;
        }

        var providedBytes = Encoding.UTF8.GetBytes(apikey);
        ClientKeyContext? match = null;

        // Iterate every configured key (never short-circuit on the first match) so the total
        // comparison time does not vary with which key (if any) matched.
        foreach (var (name, keyBytes) in _keys)
        {
            var isMatch = providedBytes.Length == keyBytes.Length
                && CryptographicOperations.FixedTimeEquals(providedBytes, keyBytes);
            if (isMatch)
            {
                match = new ClientKeyContext(name);
            }
        }

        return match;
    }
}

/// <summary>A single named client apikey, as bound from configuration.</summary>
/// <param name="Name">The key's configured name (surfaced to future M4 filter-profile association).</param>
/// <param name="Key">The literal key value clients must present.</param>
public sealed record NamedClientApiKey(string Name, string Key);
