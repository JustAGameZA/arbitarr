namespace Arbitarr.Host.Provisioning;

/// <summary>
/// M7-9: first-run scaffolding for the runtime <c>/config</c> volume (AC21). Datasets themselves
/// (e.g. AniDB's anime-lists mapping via <c>AnimeListsProvider</c>) are fetched on demand at
/// runtime and never vendored into the image or pre-seeded here (AC19) — this type only ensures
/// the directory structure a fresh, empty volume needs exists before anything tries to read or
/// write into it, so a first container start on an empty bind mount doesn't fail with
/// "directory not found" instead of a clear provisioning step.
/// </summary>
public static class DatasetProvisioner
{
    /// <summary>Subdirectories created (if absent) under the config directory on every startup.</summary>
    private static readonly string[] Subdirectories = ["datasets"];

    /// <summary>
    /// Ensures <paramref name="configDirectory"/> and its known subdirectories exist. Idempotent —
    /// safe to call on every startup, not just the first.
    /// </summary>
    public static void EnsureProvisioned(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        Directory.CreateDirectory(configDirectory);
        foreach (var subdirectory in Subdirectories)
        {
            Directory.CreateDirectory(Path.Combine(configDirectory, subdirectory));
        }
    }
}
