using System.Security.Cryptography;
using Arbitarr.Data.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #56: the scheduled backup that rides the maintenance cadence, and its bounded retention.
///
/// Retention is asserted as "which archives SURVIVE", not merely "how many files remain". A count
/// assertion passes just as happily for an implementation that keeps the OLDEST N — which would
/// leave an operator with a directory of ancient backups and no recent one, the exact opposite of
/// the guarantee.
/// </summary>
public sealed class AutomaticBackupJobTests : IDisposable
{
    private readonly string _configDirectory;
    private readonly BackupPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));

    public AutomaticBackupJobTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-auto-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);
        _paths = new BackupPaths(_configDirectory);

        SeedLiveState();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_retained_count_of_zero_disables_automatic_backups()
    {
        var state = new BackupStateStore();
        var result = await NewJob(state).RunAsync(retainedCount: 0);

        Assert.False(result.BackupTaken);
        Assert.Null(state.LastBackup);
        Assert.False(Directory.Exists(_paths.BackupDirectory));
    }

    [Fact]
    public async Task Turning_automatic_backups_off_does_not_delete_the_archives_already_taken()
    {
        // Setting the count to 0 is "stop taking new ones", not "destroy my backups". An operator
        // who disables the feature to stop the disk growing would be very surprised to find the
        // existing safety net swept along with it.
        var state = new BackupStateStore();
        var job = NewJob(state);

        await job.RunAsync(retainedCount: 3);
        var takenBefore = Directory.GetFiles(_paths.BackupDirectory).Length;
        Assert.Equal(1, takenBefore);

        await job.RunAsync(retainedCount: 0);

        Assert.Equal(takenBefore, Directory.GetFiles(_paths.BackupDirectory).Length);
    }

    [Fact]
    public async Task A_backup_is_taken_and_the_last_backup_time_is_recorded()
    {
        var state = new BackupStateStore();
        var result = await NewJob(state).RunAsync(retainedCount: 5);

        Assert.True(result.BackupTaken);
        Assert.Equal(_time.GetUtcNow(), state.LastBackup!.TakenAt);
        Assert.True(state.LastBackup.Automatic);

        var archives = Directory.GetFiles(_paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "*.zip");
        Assert.Single(archives);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_archives_and_prunes_the_rest()
    {
        var state = new BackupStateStore();
        var job = NewJob(state);

        // Five passes an hour apart. The clock is advanced rather than the files being touched, so
        // this exercises the same naming and ordering the real cadence produces.
        var names = new List<string>();
        for (var pass = 0; pass < 5; pass++)
        {
            await job.RunAsync(retainedCount: 3);
            names.Add(BackupPaths.AutomaticFilePrefix +
                _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'") + ".zip");
            _time.Advance(TimeSpan.FromHours(1));

            // The archives are distinguished by mtime, which on a fast machine would otherwise be
            // identical across passes and make the ordering assertion below meaningless.
            await Task.Delay(15);
        }

        var survivors = Directory
            .GetFiles(_paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "*.zip")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(3, survivors.Count);

        // The three NEWEST survive and the two oldest are gone — the property a bare count cannot
        // distinguish from the exactly-wrong implementation.
        Assert.Contains(names[4], survivors);
        Assert.Contains(names[3], survivors);
        Assert.Contains(names[2], survivors);
        Assert.DoesNotContain(names[1], survivors);
        Assert.DoesNotContain(names[0], survivors);
    }

    [Fact]
    public async Task Retention_never_prunes_the_pre_restore_safety_copy()
    {
        // The pre-restore copy shares this directory and is the one archive an operator reaches for
        // when a restore went wrong — precisely when several automatic backups have pushed it down
        // the list. Sweeping it would delete the recovery path at the moment it is needed.
        var state = new BackupStateStore();
        var job = NewJob(state);

        await new BackupService(_paths, _time).WriteArchiveAsync(_paths.PreRestorePath);
        Assert.True(File.Exists(_paths.PreRestorePath));

        for (var pass = 0; pass < 4; pass++)
        {
            await job.RunAsync(retainedCount: 1);
            _time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(15);
        }

        Assert.True(File.Exists(_paths.PreRestorePath));
    }

    /// <summary>
    /// A failing pass is RECORDED, not just logged — and the record is what a stale last-backup
    /// timestamp needs in order to be readable as broken rather than merely quiet.
    ///
    /// <para>POSITIVE CONTROL FIRST: the store is shown to report no failure while the pass is
    /// healthy. Without that, "a failure is recorded" would pass just as happily for a store that
    /// reports a failure unconditionally, which is the same vacuity an absence assertion has when
    /// nothing was ever in play.</para>
    /// </summary>
    [Fact]
    public void A_failed_backup_pass_is_recorded_and_a_later_success_clears_it()
    {
        var state = new BackupStateStore();
        var succeededAt = new DateTimeOffset(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

        // POSITIVE CONTROL: healthy means no failure reported, so the assertion below distinguishes
        // a recorded failure from a store that always claims one.
        state.RecordBackup(succeededAt, automatic: true);
        Assert.Null(state.LastBackupFailure);

        var failedAt = succeededAt.AddHours(1);
        state.RecordBackupFailure(failedAt, "IOException: There is not enough space on the disk.");

        Assert.NotNull(state.LastBackupFailure);
        Assert.Equal(failedAt, state.LastBackupFailure!.AttemptedAt);
        Assert.Contains("not enough space", state.LastBackupFailure.Reason, StringComparison.Ordinal);

        // The last SUCCESSFUL backup is untouched by a later failure. It is what the operator is
        // actually relying on, and overwriting it would erase the only true statement on the panel.
        Assert.Equal(succeededAt, state.LastBackup!.TakenAt);

        // A success clears the failure: a warning that outlives the problem trains an operator to
        // ignore warnings, which is worse than showing none.
        state.RecordBackup(failedAt.AddHours(1), automatic: true);
        Assert.Null(state.LastBackupFailure);
    }

    [Fact]
    public void The_state_store_reconciles_the_last_backup_time_from_archives_already_on_disk()
    {
        // A restart must not report "never backed up" beside a directory full of backups.
        Directory.CreateDirectory(_paths.BackupDirectory);
        var archive = Path.Combine(
            _paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "20260907T000000Z.zip");
        File.WriteAllText(archive, "archive");

        var written = new DateTime(2026, 9, 7, 11, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(archive, written);

        var state = new BackupStateStore();
        Assert.Null(state.LastBackup);

        state.ReconcileFromDisk(_paths);

        Assert.Equal(new DateTimeOffset(written, TimeSpan.Zero), state.LastBackup!.TakenAt);
        Assert.True(state.LastBackup.Automatic);
    }

    [Fact]
    public void A_manual_backup_taken_after_an_automatic_one_becomes_the_reported_last_backup()
    {
        // The operator asked "how fresh is my most recent backup", not "my most recent scheduled
        // one". A store that let a stale automatic timestamp win would report a safety net older
        // than the one that actually exists.
        var state = new BackupStateStore();
        var earlier = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

        state.RecordBackup(earlier, automatic: true);
        state.RecordBackup(earlier.AddHours(1), automatic: false);

        Assert.Equal(earlier.AddHours(1), state.LastBackup!.TakenAt);
        Assert.False(state.LastBackup.Automatic);

        // And an older record arriving late does not move it backwards.
        state.RecordBackup(earlier.AddMinutes(-30), automatic: true);
        Assert.Equal(earlier.AddHours(1), state.LastBackup!.TakenAt);
    }

    /// <summary>
    /// arb-gk6: THE regression test. A manual download's instant used to be in-memory only, so a
    /// fresh <see cref="BackupStateStore"/> (standing in for a process restart) reconciled from disk
    /// would see nothing at all — the download had left no trace outside the process that took it.
    /// </summary>
    [Fact]
    public void A_manual_download_survives_a_restart_as_the_last_backup_time()
    {
        var takenAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        var beforeRestart = new BackupStateStore();
        beforeRestart.RecordManualDownload(takenAt, _paths);

        // A fresh instance over the same directory, standing in for the process restart.
        var afterRestart = new BackupStateStore();
        Assert.Null(afterRestart.LastBackup);

        afterRestart.ReconcileFromDisk(_paths);

        Assert.Equal(takenAt, afterRestart.LastBackup!.TakenAt);
        Assert.False(afterRestart.LastBackup.Automatic);
    }

    /// <summary>
    /// arb-gk6, the HEADLINE scenario from the bead, end to end: automatic backups switched off
    /// (retention 0), an operator takes a manual download, the process restarts. The Backup tab
    /// used to report "never backed up" right beside a download taken five minutes earlier.
    ///
    /// <para>Distinct from the test above, not a duplicate of it. That one records a manual
    /// download into a directory the automatic job has already populated; this one covers the
    /// state <see cref="BackupStateStore.RecordManualDownload"/> newly creates — a backup
    /// directory containing the state file and NO archive at all, which exists only because that
    /// method now calls <c>Directory.CreateDirectory</c>. Reconciliation there has no automatic
    /// archive to fall back on, so the manual instant is the only thing that can answer "when was
    /// the last backup", and nothing else in this file exercises that shape.</para>
    /// </summary>
    [Fact]
    public async Task A_manual_download_survives_a_restart_when_automatic_backups_are_disabled()
    {
        var takenAt = new DateTimeOffset(2026, 9, 8, 15, 30, 0, TimeSpan.Zero);

        // Retention 0 means "stop taking new ones": the job writes no archive at all, so the
        // directory ends up holding the manual-download state file and nothing else.
        var beforeRestart = new BackupStateStore();
        var result = await NewJob(beforeRestart).RunAsync(retainedCount: 0);
        Assert.False(result.BackupTaken);

        beforeRestart.RecordManualDownload(takenAt, _paths);

        // The precondition that makes this shape distinct: no automatic archive exists to be
        // reconciled from, so the assertion below cannot pass on one by accident.
        Assert.Empty(Directory.EnumerateFiles(
            _paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "*.zip"));

        var afterRestart = new BackupStateStore();
        Assert.Null(afterRestart.LastBackup);

        afterRestart.ReconcileFromDisk(_paths);

        Assert.Equal(takenAt, afterRestart.LastBackup!.TakenAt);
        Assert.False(afterRestart.LastBackup.Automatic);
    }

    /// <summary>
    /// arb-89u: a manual archive placed in the backup directory (hand-copied, or otherwise not
    /// produced by <see cref="AutomaticBackupJob"/> or a tracked manual download) is neither
    /// reconciled into <see cref="BackupStateStore.LastBackup"/> nor swept by retention. Retention
    /// only ever globs <see cref="BackupPaths.AutomaticFilePrefix"/>, so this also doubles as the
    /// non-vacuous half: the file's continued existence after several retention passes proves
    /// retention never even considered it, not merely that this run happened not to reach it.
    ///
    /// <para><b>Positive control first.</b> "LastBackup did not pick this file up" is an absence
    /// assertion, and an absence assertion passes just as happily against a
    /// <see cref="BackupStateStore.ReconcileFromDisk"/> that picks up NOTHING — a scan that was
    /// broken outright, or a directory it never read, would satisfy it too. So the test plants an
    /// <c>auto-</c>-prefixed archive carrying the SAME late mtime first and proves reconciliation
    /// does reach that timestamp, establishing the assertion can fail, before showing the
    /// differently-named file at the identical mtime is passed over. Only the pair distinguishes
    /// "deliberately ignored because of its name" from "nothing was scanned at all".</para>
    /// </summary>
    [Fact]
    public async Task A_manual_archive_in_the_backup_directory_is_neither_reconciled_nor_deleted_by_retention()
    {
        Directory.CreateDirectory(_paths.BackupDirectory);
        var lateWriteTime = new DateTime(2026, 9, 8, 23, 0, 0, DateTimeKind.Utc);
        var lateInstant = new DateTimeOffset(lateWriteTime, TimeSpan.Zero);

        // POSITIVE CONTROL: the same mtime on an auto-prefixed name IS reconciled. Without this,
        // every "not reconciled" assertion below would also pass against a ReconcileFromDisk that
        // scanned nothing whatsoever.
        var control = Path.Combine(
            _paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "positive-control.zip");
        File.WriteAllText(control, "automatic archive");
        File.SetLastWriteTimeUtc(control, lateWriteTime);

        var controlState = new BackupStateStore();
        controlState.ReconcileFromDisk(_paths);
        Assert.Equal(lateInstant, controlState.LastBackup!.TakenAt);

        // The control has served its purpose; the real subject must be the ONLY file present, or
        // its own "not reconciled" assertion could be satisfied by the control's absence instead.
        File.Delete(control);

        var manualArchive = Path.Combine(_paths.BackupDirectory, "my-own-copy.zip");
        File.WriteAllText(manualArchive, "hand-placed archive");
        File.SetLastWriteTimeUtc(manualArchive, lateWriteTime);

        var state = new BackupStateStore();
        state.ReconcileFromDisk(_paths);

        // The identical mtime that just moved LastBackup for the auto-prefixed name leaves it null
        // here. The ONLY difference between the two files is the name.
        Assert.Null(state.LastBackup);

        var job = NewJob(state);
        for (var pass = 0; pass < 3; pass++)
        {
            await job.RunAsync(retainedCount: 1);
            _time.Advance(TimeSpan.FromHours(1));
            await Task.Delay(15);
        }

        // Not deleted: retention only globs BackupPaths.AutomaticFilePrefix, so a file without that
        // prefix is never even a candidate, no matter how many passes run.
        Assert.True(File.Exists(manualArchive));

        // Not reconciled either: LastBackup reflects the automatic archives this job took, not the
        // hand-placed file's mtime — which is LATER than any of them, so "newest wins" would have
        // surfaced it had it ever been a candidate. The positive control above proves that exact
        // instant is reachable through reconciliation when the name carries the automatic prefix.
        Assert.True(state.LastBackup!.Automatic);
        Assert.NotEqual(lateInstant, state.LastBackup.TakenAt);
        Assert.True(state.LastBackup.TakenAt < lateInstant);
    }

    private AutomaticBackupJob NewJob(BackupStateStore state) =>
        new(_paths, new BackupService(_paths, _time), state, _time);

    private void SeedLiveState()
    {
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE marker (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        // Throwaway key material, generated per test run — never a committed secret (plan §9).
        File.WriteAllBytes(_paths.SecretKeyPath, RandomNumberGenerator.GetBytes(32));
    }
}
