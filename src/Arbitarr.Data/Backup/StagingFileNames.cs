namespace Arbitarr.Data.Backup;

/// <summary>
/// The file-name prefixes every writer into <see cref="BackupPaths.StagingDirectory"/> uses,
/// collected in ONE place (arb-fxw) so the writers and <c>StagingSweepService</c> cannot
/// drift apart. Before this type existed each writer spelled its own prefix literal inline; a
/// sweep written against a second, independently-typed copy of those four strings would silently
/// stop matching the moment either side was edited, and nothing would fail loudly to say so — the
/// sweep would just quietly stop collecting orphans of whichever prefix drifted. Referencing these
/// constants from both sides makes that impossible by construction.
/// </summary>
public static class StagingFileNames
{
    /// <summary>Prefix of the archive <c>AdminBackupEndpoints</c>' download route builds via <see cref="BackupService"/>.</summary>
    public const string DownloadPrefix = "arbitarr-download-";

    /// <summary>Prefix of the spooled upload <c>AdminBackupEndpoints</c>' restore route stages before validation.</summary>
    public const string UploadPrefix = "arbitarr-upload-";

    /// <summary>Prefix of the <c>.db</c>/<c>.key</c> pair <see cref="BackupArchiveValidator"/> extracts an archive into while it is checked.</summary>
    public const string RestoreValidatePrefix = "arbitarr-restore-validate-";

    /// <summary>Prefix of the SQLite snapshot <see cref="BackupService"/> takes before zipping it.</summary>
    public const string SnapshotPrefix = "arbitarr-snapshot-";

    /// <summary>
    /// Every prefix a writer into <see cref="BackupPaths.StagingDirectory"/> uses, for
    /// <c>StagingSweepService</c> to enumerate. Adding a fifth writer means adding its
    /// prefix here AND to this array, or the sweep will not reclaim its orphans.
    /// </summary>
    public static readonly IReadOnlyList<string> AllPrefixes =
    [
        DownloadPrefix,
        UploadPrefix,
        RestoreValidatePrefix,
        SnapshotPrefix,
    ];
}
