using Microsoft.Extensions.Logging;

namespace Arbitarr.Data.Backup;

/// <summary>
/// arb-fxw: reclaims orphaned files left behind in <see cref="BackupPaths.StagingDirectory"/> by a
/// hard kill mid-restore/backup. Every writer into that directory (<see cref="StagingFileNames"/>)
/// already cleans up in a <c>finally</c>, but a <c>finally</c> only runs if the process survives
/// long enough to reach it — a crash, an OOM kill, or a forced container stop mid-operation leaves
/// the file behind with nothing left to reclaim it. Unlike the machine-wide OS temp directory this
/// replaced (arb-3gd), <see cref="BackupPaths.StagingDirectory"/> lives on the config volume, which
/// is not reliably cleared between runs — so without this sweep an orphan is permanent.
///
/// <para><b>Called once, at startup, by <see cref="StagingSweepService"/>.</b> This type is the
/// pure logic (so it is testable without spinning up a host); the hosted service is only the
/// startup-time trigger.</para>
///
/// <para><b>Only files older than process start are touched.</b> A file created by an operation
/// racing the sweep at startup (unlikely, but not impossible if the host restarts quickly) must
/// never be deleted out from under it — comparing against <see cref="File.GetLastWriteTimeUtc"/>
/// rather than "everything currently in the directory" is what keeps the sweep from being able to
/// delete a file its own process is still writing.</para>
/// </summary>
public static class StagingSweep
{
    /// <summary>
    /// Deletes every file directly under <paramref name="stagingDirectory"/> whose name starts with
    /// one of <see cref="StagingFileNames.AllPrefixes"/> and whose last-write time is strictly
    /// before <paramref name="processStartUtc"/>. Returns the number deleted.
    ///
    /// <para>A missing directory is not an error — nothing has ever staged anything, and this must
    /// not create the directory merely to find it empty (<see cref="BackupPaths.StagingDirectory"/>
    /// is created lazily, on first use, by <see cref="BackupPaths.EnsureStagingDirectory"/>).</para>
    ///
    /// <para>A single file that cannot be deleted (locked, permission denied) is logged at Warning,
    /// by name, and swept past — it must not stop the rest of the directory from being reclaimed,
    /// and it must not throw out of a startup path.</para>
    /// </summary>
    public static int Run(string stagingDirectory, DateTime processStartUtc, ILogger logger) =>
        Run(stagingDirectory, processStartUtc, logger, File.Delete);

    /// <summary>
    /// Same as <see cref="Run(string, DateTime, ILogger)"/>, with the delete action replaceable by a
    /// caller. Production always uses the default overload above, which passes <see cref="File.Delete(string)"/>
    /// directly. This overload exists for <c>StagingSweepTests</c>: proving the per-file
    /// warn-and-continue path deterministically needs a delete that is GUARANTEED to throw for one
    /// specific file on every platform, and neither an open <c>FileStream</c> with
    /// <see cref="FileShare.None"/> nor the read-only file attribute reliably makes
    /// <see cref="File.Delete(string)"/> throw on Linux — a second handle from the SAME process is not
    /// blocked by an earlier <c>FileShare.None</c> there, and Linux <c>unlink()</c> permission comes
    /// from the DIRECTORY, not the file's own read-only bit, so .NET does not surface it as a delete
    /// failure the way Windows does. Injecting the action sidesteps the OS-level lock entirely: the
    /// test is about the catch-and-continue behaviour, not about reproducing a real lock.
    /// </summary>
    public static int Run(string stagingDirectory, DateTime processStartUtc, ILogger logger, Action<string> deleteFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(deleteFile);

        if (!Directory.Exists(stagingDirectory))
        {
            logger.LogInformation("Staging sweep: {Count} orphaned file(s) deleted (no staging directory yet).", 0);
            return 0;
        }

        var deleted = 0;

        foreach (var path in Directory.EnumerateFiles(stagingDirectory))
        {
            var name = Path.GetFileName(path);

            if (!StagingFileNames.AllPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Staging sweep: could not read the last-write time of {FileName}; leaving it in place.", name);
                continue;
            }

            if (lastWriteUtc >= processStartUtc)
            {
                // Written at or after this process started -- an in-flight operation on this very
                // run, not an orphan from a previous one. Never touch it.
                continue;
            }

            try
            {
                deleteFile(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Staging sweep: could not delete orphaned staging file {FileName}.", name);
            }
        }

        logger.LogInformation("Staging sweep: {Count} orphaned file(s) deleted.", deleted);
        return deleted;
    }
}
