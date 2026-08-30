using System.Security.Cryptography;

namespace Arbitarr.Host.Security;

/// <summary>
/// Generates (on first run) and loads (on subsequent runs) the per-instance HMAC secret used by
/// <see cref="Arbitarr.Api.Rendering.ReleaseGuid"/> (SEC-L2). Persisted as a raw 32-byte file
/// under the configured config directory so it survives restarts — Sonarr/Radarr grab history and
/// pagination snapshots reference proxy guids computed from it long after the request that
/// produced them. Never logged; never committed (lives under the runtime /config volume, not the
/// repo).
/// </summary>
public static class ReleaseGuidSecretFile
{
    private const string FileName = "release-guid-secret.key";

    /// <summary>Loads the persisted secret from <paramref name="configDirectory"/>, generating and persisting a new one if none exists yet.</summary>
    public static byte[] LoadOrCreate(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, FileName);

        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var generated = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, generated);
        return generated;
    }
}
