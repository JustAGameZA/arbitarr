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
    /// Seeds <see cref="LastBackup"/> from the newest automatic archive already on disk, so a
    /// restart does not report "never backed up" beside a directory full of backups. Only automatic
    /// archives are considered: a manual download is streamed and never kept server-side, so there
    /// is no file whose age could stand in for it.
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
    }
}
