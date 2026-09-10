using Arbitarr.Data.Backup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-fxw: <see cref="StagingSweep.Run"/> deletes orphaned staging files left by a hard kill
/// mid-restore/backup, and ONLY those. Every "does not touch X" case here has a positive control
/// (CLAUDE.md §4): the same run must both delete what it should and, in the same pass, prove it
/// left everything else alone, so a sweep that deleted the whole directory could not pass silently.
/// </summary>
public sealed class StagingSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arb-fxw-sweep-tests", Guid.NewGuid().ToString("N"));

    private string StagingDir => Path.Combine(_root, "backup-staging");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file on Windows shouldn't fail the test run.
        }
    }

    [Fact]
    public void Deletes_old_prefixed_files_and_leaves_recent_non_prefixed_and_outside_files_alone()
    {
        Directory.CreateDirectory(StagingDir);
        var processStart = DateTime.UtcNow;

        // One OLD file per known prefix -- these are the orphans the sweep exists to reclaim.
        var oldFiles = StagingFileNames.AllPrefixes
            .Select(prefix => WriteFile(StagingDir, prefix + Guid.NewGuid().ToString("N"), ageMinutes: 30))
            .ToArray();

        // A RECENT prefixed file -- written after process start, so it could be an in-flight
        // operation on THIS run. Must survive.
        var recentPrefixed = WriteFile(StagingDir, StagingFileNames.DownloadPrefix + "recent", ageMinutes: 0);

        // A non-prefixed file inside the staging directory -- never written by any known staging
        // writer. Must survive.
        var nonPrefixed = WriteFile(StagingDir, "not-a-staging-file.txt", ageMinutes: 30);

        // A PREFIXED, OLD file in a SIBLING directory outside staging -- the positive control that
        // proves the sweep is scoped to the directory it was given, not to the prefix alone.
        var siblingDir = Path.Combine(_root, "backups");
        Directory.CreateDirectory(siblingDir);
        var outsideFile = WriteFile(siblingDir, StagingFileNames.DownloadPrefix + "outside", ageMinutes: 30);

        var deleted = StagingSweep.Run(StagingDir, processStart, NullLogger.Instance);

        Assert.Equal(oldFiles.Length, deleted);
        foreach (var path in oldFiles)
        {
            Assert.False(File.Exists(path), $"Expected old staging file to be deleted: {Path.GetFileName(path)}");
        }

        Assert.True(File.Exists(recentPrefixed), "A recent prefixed file must not be deleted.");
        Assert.True(File.Exists(nonPrefixed), "A non-prefixed file inside staging must not be deleted.");
        Assert.True(File.Exists(outsideFile), "A prefixed file outside the staging directory must not be deleted.");
    }

    [Fact]
    public void A_missing_staging_directory_does_not_throw_and_reports_zero()
    {
        Assert.False(Directory.Exists(StagingDir));

        var deleted = StagingSweep.Run(StagingDir, DateTime.UtcNow, NullLogger.Instance);

        Assert.Equal(0, deleted);
        Assert.False(Directory.Exists(StagingDir), "The sweep must not create the directory merely to find it empty.");
    }

    [Fact]
    public void An_undeletable_file_is_warned_about_and_skipped_while_others_are_still_deleted()
    {
        Directory.CreateDirectory(StagingDir);
        var processStart = DateTime.UtcNow;

        var lockedPath = WriteFile(StagingDir, StagingFileNames.UploadPrefix + "locked", ageMinutes: 30);
        var deletablePath = WriteFile(StagingDir, StagingFileNames.UploadPrefix + "deletable", ageMinutes: 30);

        var logger = new CapturingLogger();

        // FileShare.None on an open FileStream is NOT what makes File.Delete throw on Linux (CI's
        // runner): unlike Windows, a second handle opened from the SAME process is not blocked by
        // an earlier FileShare.None, and unlink() over an already-open file is permitted outright
        // -- File.Delete succeeds there regardless of any open FileStream. The read-only attribute,
        // by contrast, is enforced by File.Delete on both platforms (UnauthorizedAccessException),
        // which is what actually reproduces the per-file "could not delete, warn and move on" path
        // this test exists to prove.
        File.SetAttributes(lockedPath, File.GetAttributes(lockedPath) | FileAttributes.ReadOnly);

        try
        {
            var deleted = StagingSweep.Run(StagingDir, processStart, logger);

            Assert.Equal(1, deleted);
            Assert.True(File.Exists(lockedPath), "The undeletable file must survive the sweep.");
            Assert.False(File.Exists(deletablePath), "The deletable sibling must still be deleted.");
        }
        finally
        {
            File.SetAttributes(lockedPath, File.GetAttributes(lockedPath) & ~FileAttributes.ReadOnly);
        }

        Assert.Contains(
            logger.Warnings,
            w => w.Contains(Path.GetFileName(lockedPath), StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_zero_and_logs_an_information_line_when_nothing_is_orphaned()
    {
        Directory.CreateDirectory(StagingDir);
        var logger = new CapturingLogger();

        var deleted = StagingSweep.Run(StagingDir, DateTime.UtcNow, logger);

        Assert.Equal(0, deleted);
        Assert.Single(logger.Informations);
    }

    private static string WriteFile(string directory, string name, int ageMinutes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "staging test content");

        if (ageMinutes > 0)
        {
            var stamp = DateTime.UtcNow.AddMinutes(-ageMinutes);
            File.SetLastWriteTimeUtc(path, stamp);
        }

        return path;
    }

    /// <summary>
    /// A minimal <see cref="ILogger"/> that records formatted Warning/Information messages, so the
    /// per-file-warning and one-line-summary contract can be asserted directly rather than trusted.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public List<string> Informations { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);

            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(message);
            }
            else if (logLevel == LogLevel.Information)
            {
                Informations.Add(message);
            }
        }
    }
}
