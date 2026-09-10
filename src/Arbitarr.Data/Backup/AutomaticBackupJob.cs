using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Data.Backup;

/// <summary>What one automatic-backup pass did.</summary>
/// <param name="BackupTaken">False when retention is set to 0, which disables automatic backups.</param>
/// <param name="ArchivesPruned">How many archives beyond the retained count were deleted.</param>
public sealed record AutomaticBackupResult(bool BackupTaken, int ArchivesPruned);

/// <summary>
/// Takes a scheduled backup into the config directory and prunes older ones to a bounded count
/// (#56's periodic-backup acceptance criterion).
///
/// <para><b>It rides <c>MaintenanceJob</c>'s existing cadence rather than owning a timer.</b> The
/// same argument #65 made when it put the log-database trim on this schedule: it is the same kind
/// of work — bounded-retention housekeeping over the config directory — and a second scheduler
/// would be a second thing to reason about when the disk fills. It is called from
/// <c>MaintenanceHostedService</c> in its own try/catch, so a failing backup cannot stop the
/// database from being pruned, and reported in <c>MaintenanceJobResult</c> so a pass that silently
/// took no backup is visible rather than assumed.</para>
///
/// <para><b>Retention is a count, not an age.</b> A time-based policy on a homelab box can delete
/// the last backup an operator has when the machine has simply been off; keeping the N newest
/// cannot. <c>SettingKey.AutomaticBackupRetainedCount</c> carries N, and setting it to 0 turns
/// automatic backups off entirely — an operator who backs up externally should not be made to store
/// a second copy of their credentials on the same disk.</para>
///
/// <para><b>These archives are as sensitive as the downloaded one</b> — same HMAC secret, same
/// source API keys — and live under the config directory, which is not served. That is why they go
/// in <see cref="BackupPaths.BackupDirectory"/> and nowhere near <c>wwwroot</c>.</para>
/// </summary>
public sealed class AutomaticBackupJob
{
    private readonly BackupPaths _paths;
    private readonly BackupService _backupService;
    private readonly BackupStateStore _state;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    // Never mutated after construction in production; only WithDeleteFileForTests writes it.
    private Action<FileInfo> _deleteFile = file => file.Delete();

    public AutomaticBackupJob(
        BackupPaths paths,
        BackupService backupService,
        BackupStateStore state,
        ILogger<AutomaticBackupJob> logger,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Takes one automatic backup and prunes to <paramref name="retainedCount"/> archives. A
    /// retained count of 0 disables the feature and takes nothing.
    /// </summary>
    public async Task<AutomaticBackupResult> RunAsync(int retainedCount, CancellationToken cancellationToken = default)
    {
        if (retainedCount <= 0)
        {
            // Off. Existing archives are left alone rather than swept: turning the feature off is
            // not an instruction to delete the backups already taken under it.
            return new AutomaticBackupResult(BackupTaken: false, ArchivesPruned: 0);
        }

        var takenAt = _timeProvider.GetUtcNow();
        var fileName = BackupPaths.AutomaticFilePrefix +
            takenAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".zip";

        await _backupService
            .WriteArchiveAsync(Path.Combine(_paths.BackupDirectory, fileName), cancellationToken)
            .ConfigureAwait(false);

        _state.RecordBackup(takenAt, automatic: true);

        var pruned = PruneToRetainedCount(retainedCount, _deleteFile);
        return new AutomaticBackupResult(BackupTaken: true, ArchivesPruned: pruned);
    }

    /// <summary>
    /// Keeps the <paramref name="retainedCount"/> newest automatic archives and deletes the rest.
    ///
    /// Only files carrying <see cref="BackupPaths.AutomaticFilePrefix"/> are considered. The
    /// pre-restore safety copy shares this directory and must never be swept by retention: it is
    /// the one archive an operator reaches for when a restore went wrong, and the moment it is most
    /// needed is exactly when several automatic backups have since pushed it down a list.
    ///
    /// <para><paramref name="deleteFile"/> is production's <see cref="FileInfo.Delete()"/> by
    /// default (see the field initializer). <c>AutomaticBackupJobTests</c> replaces it via
    /// <see cref="WithDeleteFileForTests"/> to deterministically fail one specific file: neither a
    /// held <see cref="FileStream"/> opened with <see cref="FileShare.None"/> nor the read-only
    /// file attribute reliably makes <c>Delete()</c> throw on Linux CI — a second handle from the
    /// SAME process is not blocked by an earlier <c>FileShare.None</c> there, and Linux
    /// <c>unlink()</c> permission comes from the DIRECTORY, not the file's own read-only bit. See
    /// the identical reasoning on <see cref="StagingSweep.Run(string, DateTime, ILogger, Action{string})"/>,
    /// which this mirrors.</para>
    /// </summary>
    private int PruneToRetainedCount(int retainedCount, Action<FileInfo> deleteFile)
    {
        if (!Directory.Exists(_paths.BackupDirectory))
        {
            return 0;
        }

        var stale = Directory
            .EnumerateFiles(_paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "*.zip")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(retainedCount)
            .ToList();

        var pruned = 0;
        foreach (var file in stale)
        {
            try
            {
                deleteFile(file);
                pruned++;
            }
            // No OperationCanceledException guard precedes this broad catch: the loop is
            // synchronous and the cancellation token is deliberately not threaded into it. If a
            // token is ever threaded through, add the guard ahead of this broad catch.
            catch (Exception ex)
            {
                // Any delete failure (locked archive, permissions, ...) is logged and skipped
                // rather than failing the run; the backup itself already succeeded and is the
                // half that matters, and the remaining stale archives should still be pruned.
                _logger.LogWarning(
                    ex,
                    "Could not delete stale automatic backup archive {FileName}; it will be retried next pass.",
                    file.Name);
            }
        }

        return pruned;
    }

    /// <summary>
    /// Test-only seam: replaces the delete action <see cref="RunAsync"/> uses when pruning, so
    /// <c>AutomaticBackupJobTests</c> can make one specific archive's delete deterministically fail
    /// on every platform (see <see cref="PruneToRetainedCount"/>'s remarks). Production never calls
    /// this — the field's initializer, <c>file =&gt; file.Delete()</c>, is what every real run uses.
    /// </summary>
    public void WithDeleteFileForTests(Action<FileInfo> deleteFile)
    {
        _deleteFile = deleteFile ?? throw new ArgumentNullException(nameof(deleteFile));
    }
}
