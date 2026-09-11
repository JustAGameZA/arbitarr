using System.Security.Cryptography;
using System.Text;
using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Computes a stable, opaque identifier for a merged release, used as the download-proxy
/// lookup key (see DownloadProxyEndpoint). The identity is derived from the upstream source
/// name plus the upstream's own guid, so the same upstream release always maps to the same
/// proxy guid across requests, while remaining independent of any rendering/normalization
/// applied to the release's other fields.
///
/// SEC-L2: the hash is HMAC-SHA256 keyed by a per-instance secret rather than unsalted SHA-256,
/// so proxy guids cannot be predicted/enumerated by anyone who doesn't know the secret. The
/// secret defaults to a random value generated at process start (via <c>IReleaseGuidSecretProvider</c>
/// implementations, e.g. Arbitarr.Host's persisted-under-/config provider) and must be configured
/// once via <see cref="Configure"/> before any guid computation, so that guids stay stable across
/// requests within a single running instance (and, when backed by a persisted secret, across
/// restarts — required because Sonarr grab history and pagination snapshots reference guids
/// computed from it long after the request that produced them).
/// </summary>
public static class ReleaseGuid
{
    private static byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Sets the per-instance HMAC secret used by subsequent <see cref="Compute"/> calls. Must be
    /// called once, early in startup (before any guid is computed), with a secret that is stable
    /// across restarts of this instance.
    /// </summary>
    public static void Configure(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0)
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        _hmacKey = secret;
    }

    /// <summary>
    /// Computes a stable, hex-encoded HMAC-SHA256 of the release's identity
    /// (source name + upstream guid), keyed by the configured per-instance secret.
    /// </summary>
    public static string Compute(ReleaseIdentity identity)
    {
        var input = $"{identity.SourceName} {identity.Guid}";
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = HMACSHA256.HashData(_hmacKey, bytes);
        return Convert.ToHexStringLower(hash);
    }
}
