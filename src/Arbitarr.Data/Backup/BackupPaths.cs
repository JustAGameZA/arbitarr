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

    /// <summary>
    /// The subdirectory under the config directory holding transient restore/backup working files
    /// (arb-3gd): the upload spool, the validator's extraction, the download build, and the snapshot
    /// <see cref="BackupService"/> takes before zipping it.
    ///
    /// <para>Deliberately a SIBLING of <see cref="BackupSubdirectoryName"/>, not a child of it: the
    /// backup archive is built from named files only (<see cref="DatabasePath"/>,
    /// <see cref="SecretKeyPath"/>) and never by enumerating a directory, so nothing here can leak
    /// into an archive by that route — but keeping the two directories apart means a future change
    /// that DOES walk <see cref="BackupDirectory"/> (a retention sweep, an export) still cannot pick
    /// up an in-flight staging file by accident. It is also, like <see cref="BackupDirectory"/>, NOT
    /// under <c>wwwroot</c> and not otherwise served.</para>
    ///
    /// <para>Per-instance rather than the machine-wide <c>Path.GetTempPath()</c> the four sites used
    /// before: the shared system temp directory made a "nothing was staged" assertion process-global,
    /// so any other process (or another test host in the same run) spooling an upload with the same
    /// prefix broke it — see arb-3gd. Scoping it under this instance's own config directory makes the
    /// assertion instance-scoped instead, which is what makes it parallel-safe.</para>
    /// </summary>
    public const string StagingSubdirectoryName = "backup-staging";

    public BackupPaths(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        ConfigDirectory = configDirectory;
        DatabasePath = Path.Combine(configDirectory, "arbitarr.db");
        SecretKeyPath = Path.Combine(configDirectory, "release-guid-secret.key");
        BackupDirectory = Path.Combine(configDirectory, BackupSubdirectoryName);
        StagingDirectory = Path.Combine(configDirectory, StagingSubdirectoryName);
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

    /// <summary>
    /// Where restore/backup working files are staged (see <see cref="StagingSubdirectoryName"/> for
    /// why this directory and not <c>Path.GetTempPath()</c>). Not guaranteed to exist; callers create
    /// it on first use via <see cref="EnsureStagingDirectory"/>.
    /// </summary>
    public string StagingDirectory { get; }

    /// <summary>
    /// Creates <see cref="StagingDirectory"/> if it does not already exist and returns it. Called at
    /// each use site rather than once at startup: a config volume can be recreated or pruned under a
    /// long-running process, and every site here already tolerates the directory being absent.
    /// </summary>
    public string EnsureStagingDirectory()
    {
        Directory.CreateDirectory(StagingDirectory);
        return StagingDirectory;
    }
}
