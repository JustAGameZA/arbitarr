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
/// <para><b>The no-race mechanism is the process-start cut-off, not hosted-service ordering.</b>
/// <c>BackgroundService.ExecuteAsync</c> is NOT awaited before the host reports started (that is
/// why <c>StagingSweepIntegrationTests</c> polls rather than asserting immediately) — Kestrel can
/// already be serving requests while this pass is still enumerating the directory. What actually
/// makes that safe is that THIS service captures <c>processStartUtc</c> once, below, before calling
/// <see cref="StagingSweep.Run(string, DateTime, ILogger)"/> — <c>Run</c> itself does not capture
/// anything, it only receives the instant and skips any file whose last write is at or after it. A
/// request-path writer that starts after the cut-off therefore can never have its file swept,
/// regardless of how far startup has otherwise progressed. A per-file <c>DateTime.UtcNow</c>
/// "tidy-up" in <see cref="StagingSweep"/> would reintroduce exactly this race — see the
/// <c>&gt;=</c> comment there.</para>
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
            // Per-item error handling (docs/standards/architecture.md): an orphan left in place
            // for one more run is a much smaller problem than the host refusing to start.
            logger.LogError(ex, "Staging sweep failed at startup; will retry on the next restart.");
        }

        return Task.CompletedTask;
    }
}
