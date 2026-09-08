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
