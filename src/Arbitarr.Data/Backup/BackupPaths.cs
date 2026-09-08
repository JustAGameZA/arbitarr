namespace Arbitarr.Data.Backup;

/// <summary>
/// Where the two backed-up files live on disk, and where restore stages its work (#56).
///
/// Constructed in the Host composition root from the same <c>configDirectory</c> that
/// <c>Program.cs</c> uses for the database and the secret, so this type can never point at a
/// different directory from the running process. Registered as a singleton and injected rather
/// than each consumer recomputing paths from an environment variable.
/// </summary>
public sealed class BackupPaths
{
    /// <summary>
    /// The subdirectory under the config directory holding automatic backups and the pre-restore
    /// safety copy. NOT under <c>wwwroot</c> and not otherwise served: the archives inside it carry
    /// the HMAC secret and every configured source's API key, so a static-file mapping over this
    /// path would be a credential leak. Nothing in the request pipeline maps it — that is the
    /// property to preserve if the static-file configuration is ever revisited.
    /// </summary>
    public const string BackupSubdirectoryName = "backups";

    /// <summary>The name the pre-restore safety copy is always written under.</summary>
    public const string PreRestoreFileName = "pre-restore.zip";

    /// <summary>Prefix of an automatic (scheduled) backup, used to identify them for retention.</summary>
    public const string AutomaticFilePrefix = "auto-";

    public BackupPaths(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        ConfigDirectory = configDirectory;
        DatabasePath = Path.Combine(configDirectory, "arbitarr.db");
        SecretKeyPath = Path.Combine(configDirectory, "release-guid-secret.key");
        BackupDirectory = Path.Combine(configDirectory, BackupSubdirectoryName);
    }

    /// <summary>The runtime config directory (the <c>/config</c> bind mount in production).</summary>
    public string ConfigDirectory { get; }

    /// <summary>
    /// The configuration database. Note there are TWO SQLite files under
    /// <see cref="ConfigDirectory"/>: this one and the separate application-log store named by
    /// <see cref="Arbitarr.Data.Logging.LogStore.DatabaseFileName"/>. The log store is deliberately
    /// outside every backup — see <see cref="BackupArchiveLayout"/> for the reasoning, and grep for
    /// that constant rather than for "arbitarr.db" when enumerating stores.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>The per-instance release-GUID HMAC secret, as written by <c>ReleaseGuidSecretFile</c>.</summary>
    public string SecretKeyPath { get; }

    /// <summary>Where automatic backups and the pre-restore safety copy are kept.</summary>
    public string BackupDirectory { get; }

    /// <summary>The path the pre-restore safety copy is written to before a restore is applied.</summary>
    public string PreRestorePath => Path.Combine(BackupDirectory, PreRestoreFileName);
}
