using Arbitarr.Data.Backup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Host.Backup;

/// <summary>
/// arb-fxw: runs <see cref="StagingSweep.Run"/> once, at startup, against THIS instance's own
/// <see cref="BackupPaths.StagingDirectory"/> — never the system temp dir, never
/// <see cref="BackupPaths.BackupDirectory"/> or the config directory root. See
/// <see cref="StagingSweep"/> for why a hard kill mid-restore/backup needs a reclaimer beyond each
/// writer's own <c>finally</c>.
///
/// <para><b>One-shot, not a recurring timer.</b> Unlike <c>MaintenanceHostedService</c>, orphans
/// here can only be produced by a kill mid-operation, which by definition happens between process
/// lifetimes — there is nothing new to sweep while this process is still running normally, so a
/// single pass at startup is the whole job. <c>ExecuteAsync</c> runs it and returns; it does not
/// loop or hold the host's shutdown token.</para>
///
/// <para><b>Runs after other hosted services register but does not block them</b> — registered
/// alongside the other hosted services in <c>Program.cs</c>; see the comment there for ordering.
/// </para>
/// </summary>
public sealed class StagingSweepService(
    BackupPaths paths,
    TimeProvider timeProvider,
    ILogger<StagingSweepService> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // GetUtcNow() at the moment this service starts executing stands in for "process
            // start": it is captured once, here, rather than re-read per file, so every file in
            // the directory is judged against the same instant regardless of how long the
            // enumeration takes.
            StagingSweep.Run(paths.StagingDirectory, timeProvider.GetUtcNow().UtcDateTime, logger);
        }
        catch (Exception ex)
        {
            // A failed sweep must not take the host down or block startup of anything else -- an
            // orphan left in place for one more run is a much smaller problem than the host
            // refusing to start.
            logger.LogError(ex, "Staging sweep failed at startup; will retry on the next restart.");
        }

        return Task.CompletedTask;
    }
}
