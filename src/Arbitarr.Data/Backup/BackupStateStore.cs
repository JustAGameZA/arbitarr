using System.Text.Json;

namespace Arbitarr.Data.Backup;

/// <summary>When the last backup was taken, and how it was triggered.</summary>
/// <param name="TakenAt">The instant the archive was produced.</param>
/// <param name="Automatic">True for a scheduled backup, false for an operator download.</param>
public sealed record LastBackup(DateTimeOffset TakenAt, bool Automatic);

/// <summary>
/// The most recent automatic-backup FAILURE, so a stale <see cref="LastBackup"/> can be explained
/// rather than merely observed.
/// </summary>
/// <param name="AttemptedAt">When the failing pass ran.</param>
/// <param name="Reason">
/// The exception'''s type and message. A reason, never the exception object or its stack: this is
/// rendered in the UI, and a backup failure'''s message can name paths inside the config directory.
/// </param>
public sealed record LastBackupFailure(DateTimeOffset AttemptedAt, string Reason);

/// <summary>The outcome of the most recent restore attempt, for the UI to report.</summary>
/// <param name="AttemptedAt">When the attempt was made.</param>
/// <param name="Succeeded">Whether it applied.</param>
/// <param name="Message">The same operator-facing message the request returned.</param>
public sealed record LastRestore(DateTimeOffset AttemptedAt, bool Succeeded, string Message);

/// <summary>
/// In-memory record of the last backup and the last restore attempt (#56).
///
/// <para><b>Deliberately in memory, and deliberately not a settings row.</b> A restore REPLACES the
/// configuration database wholesale, so a restore outcome persisted in that database would be
/// overwritten by the very operation it is reporting on — the one moment it most needs to survive
/// is the one moment it cannot. The last-backup time is held the same way for symmetry, and because
/// it is reconstructed from the backup directory's own file times on startup (see
/// <see cref="ReconcileFromDisk"/>), which is a truer source than a row that could disagree with
/// the files on disk.</para>
///
/// <para>Consequence, stated rather than hidden: the last-RESTORE outcome does not survive the
/// restart that a restore triggers. The UI says so instead of showing a blank panel as though
/// nothing happened.</para>
/// </summary>
public sealed class BackupStateStore
{
    /// <summary>
    /// arb-gk6/arb-89u: the ONLY thing persisted to disk for this type — the last manual-download
    /// instant, as a single ISO 8601 UTC timestamp. Deliberately not a JSON document with room for
    /// more fields: this is the one piece of <see cref="BackupStateStore"/>'s otherwise in-memory
    /// state that has no other durable trace (an automatic backup's own file mtime IS its durable
    /// record; a manual download is streamed and never kept, so its instant would otherwise be lost
    /// on every restart). Lives in <see cref="BackupPaths.BackupDirectory"/> alongside the automatic
    /// archives it is the sibling record of, not in the config database — see this type's own doc
    /// comment for why a restore-surviving fact does not belong in the database a restore replaces.
    /// </summary>
    private const string ManualDownloadStateFileName = "last-manual-download.json";

    private sealed record ManualDownloadState(DateTimeOffset LastManualDownloadAt);

    private readonly object _gate = new();
    private LastBackup? _lastBackup;
    private LastBackupFailure? _lastBackupFailure;
    private LastRestore? _lastRestore;

    public LastBackup? LastBackup
    {
        get
        {
            lock (_gate)
            {
                return _lastBackup;
            }
        }
    }

    /// <summary>
    /// The last automatic backup that FAILED, or null when the most recent pass succeeded.
    ///
    /// <para>Without this, a failing scheduled backup is invisible on every operational surface: the
    /// hosted service catches, logs at Error and carries on, so <see cref="LastBackup"/> simply stops
    /// advancing and the Backup tab shows a timestamp that looks like a real backup and is in fact a
    /// safety net that has been broken since. A stale backup presented as fresh is precisely the
    /// failure #56 exists to prevent, so the degraded path carries its own provenance.</para>
    /// </summary>
    public LastBackupFailure? LastBackupFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastBackupFailure;
            }
        }
    }

    public LastRestore? LastRestore
    {
        get
        {
            lock (_gate)
            {
                return _lastRestore;
            }
        }
    }

    public void RecordBackup(DateTimeOffset takenAt, bool automatic)
    {
        lock (_gate)
        {
            // Newest wins. A manual download taken after an automatic run must not be hidden by a
            // stale automatic timestamp, and vice versa — the operator asked "how fresh is my most
            // recent backup", not "how fresh is my most recent scheduled one".
            if (_lastBackup is null || takenAt >= _lastBackup.TakenAt)
            {
                _lastBackup = new LastBackup(takenAt, automatic);
            }

            // A success CLEARS the recorded failure. Leaving it would leave the Backup tab warning
            // about a problem that has since been fixed, which trains an operator to ignore the
            // warning — worse than not showing one.
            _lastBackupFailure = null;
        }
    }

    /// <summary>
    /// Records a manual download AND persists its instant to
    /// <see cref="ManualDownloadStateFileName"/>, so it survives the restart that
    /// <see cref="ReconcileFromDisk"/> would otherwise report right past (arb-gk6). The archive
    /// itself is never kept (see <see cref="ReconcileFromDisk"/>'s doc comment), so this timestamp
    /// is the only durable trace of it — deliberately narrower than a full backup-history file.
    ///
    /// <para>A write failure (read-only volume, full disk) is swallowed rather than failing the
    /// download: the operator still gets their archive, all they lose is the state file's
    /// contribution to the NEXT restart's summary, and a backup feature must not be the reason a
    /// backup fails.</para>
    /// </summary>
    public void RecordManualDownload(DateTimeOffset takenAt, BackupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        RecordBackup(takenAt, automatic: false);

        try
        {
            Directory.CreateDirectory(paths.BackupDirectory);
            var path = Path.Combine(paths.BackupDirectory, ManualDownloadStateFileName);
            var json = JsonSerializer.Serialize(new ManualDownloadState(takenAt));
            File.WriteAllText(path, json);
        }
        catch (Exception)
        {
            // Deliberately catch-all, and deliberately placed AFTER the in-memory RecordBackup
            // above, which has already succeeded by the time anything here can throw.
            //
            // The narrow set this replaces (IOException, UnauthorizedAccessException) did not
            // cover what the doc comment promises, and the gap is not theoretical: a path too
            // long for the filesystem, a security policy refusing the directory creation, or a
            // serializer failure all escape those two. Every one of them would have propagated
            // out of a route whose job is to hand the operator an archive it has ALREADY
            // generated successfully — failing the download because the bookkeeping about it
            // could not be written is precisely the outcome this feature exists to prevent.
            // So the catch is widened to match the promise, rather than the promise narrowed
            // to match the catch.
            //
            // Nothing is logged: this type holds no logger, and the entire cost of the loss is
            // one line in the next restart's summary.
        }
    }

    /// <summary>
    /// Records that an automatic backup pass failed. Takes the reason as a string rather than an
    /// exception so the caller decides what is safe to render; see <see cref="LastBackupFailure"/>.
    /// </summary>
    public void RecordBackupFailure(DateTimeOffset attemptedAt, string reason)
    {
        lock (_gate)
        {
            _lastBackupFailure = new LastBackupFailure(attemptedAt, reason);
        }
    }

    public void RecordRestore(DateTimeOffset attemptedAt, bool succeeded, string message)
    {
        lock (_gate)
        {
            _lastRestore = new LastRestore(attemptedAt, succeeded, message);
        }
    }

    /// <summary>
    /// Seeds <see cref="LastBackup"/> from the newest automatic archive already on disk, plus the
    /// persisted last-manual-download instant (arb-gk6/arb-89u), so a restart does not report "never
    /// backed up" beside a directory full of backups OR beside a download the operator took five
    /// minutes before restarting the container. <see cref="RecordBackup"/>'s "newest wins" rule then
    /// decides which of the two is reported, exactly as it does when both happen live in one process
    /// lifetime.
    ///
    /// <para><b>Deliberately excludes any OTHER manual archive found in the backup directory</b> —
    /// a hand-copied or manually-created <c>.zip</c> dropped there is neither reconciled into
    /// <see cref="LastBackup"/> nor a candidate for <see cref="AutomaticBackupJob"/>'s retention
    /// pruning (arb-89u). Retention already only globs <see cref="BackupPaths.AutomaticFilePrefix"/>,
    /// so such a file is safe from deletion by construction; it is simply invisible to this store,
    /// the same as it always was. Reconciling arbitrary files by content or name pattern would risk
    /// either treating a stray non-backup zip as a backup, or deleting a file an operator placed
    /// there on purpose — neither is worth the very small feature (a hand-managed archive's own file
    /// timestamp, visible to anyone who lists the directory) it would buy.</para>
    /// </summary>
    public void ReconcileFromDisk(BackupPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!Directory.Exists(paths.BackupDirectory))
        {
            return;
        }

        var newest = Directory
            .EnumerateFiles(paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is not null)
        {
            RecordBackup(new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero), automatic: true);
        }

        var manualDownloadPath = Path.Combine(paths.BackupDirectory, ManualDownloadStateFileName);
        if (File.Exists(manualDownloadPath))
        {
            try
            {
                var json = File.ReadAllText(manualDownloadPath);
                var state = JsonSerializer.Deserialize<ManualDownloadState>(json);
                if (state is not null)
                {
                    RecordBackup(state.LastManualDownloadAt, automatic: false);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
                // A corrupted or hand-edited state file must not fault startup — it is a durability
                // aid for one field, not a source of truth worth crashing over.
            }
        }
    }
}
