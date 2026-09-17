using Arbitarr.Data.Backup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Host.Backup;

/// <summary>
/// arb-fxw: runs <see cref="StagingSweep.Run(string, DateTime, ILogger)"/> once, at startup, against THIS instance's own
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
/// <para><b>The cut-off protects LATER writers; the first-pass wait protects the EARLIER one.</b>
/// <c>BackgroundService.ExecuteAsync</c> is NOT awaited before the host reports started (that is
/// why <c>StagingSweepIntegrationTests</c> polls rather than asserting immediately) — Kestrel can
/// already be serving requests while this pass is still enumerating the directory. For any writer
/// that BEGINS after this service starts executing, what makes that safe is the
/// <c>processStartUtc</c> cut-off captured once, below, before calling
/// <see cref="StagingSweep.Run(string, DateTime, ILogger)"/> — <c>Run</c> itself does not capture
/// anything, it only receives the instant and skips any file whose last write is at or after it. A
/// request-path writer that starts after the cut-off therefore can never have its file swept,
/// regardless of how far startup has otherwise progressed. A per-file <c>DateTime.UtcNow</c>
/// "tidy-up" in <see cref="StagingSweep"/> would reintroduce exactly this race — see the
/// <c>&gt;=</c> comment there.</para>
///
/// <para><b>arb-07jl: the cut-off alone was NOT enough, because one writer starts BEFORE it.</b>
/// <c>MaintenanceHostedService</c>'s first pass takes an automatic backup immediately, and both
/// services are started by the same unordered hosted-service sequence. A snapshot whose last write
/// happened before this service captured its instant — and which has not been written to again
/// because <c>BackupService.WriteArchiveAsync</c> is mid-copy on it — is OLDER than the cut-off and
/// so was deleted while still open. On Linux that surfaces as SQLite error 5898
/// (<c>SQLITE_IOERR_DELETE</c>) failing the startup backup; on Windows the file-sharing rules hide
/// it. The fix is to await the first maintenance pass before capturing the instant at all, so no
/// backup of that pass can still be in flight when the sweep judges the directory. The cut-off is
/// unchanged and still does its own job for everything that starts afterwards.</para>
///
/// <para><b>Awaiting the pass never blocks startup and never fails it.</b> The waiter is the
/// <c>FirstPassCompleted</c> seam, which is published from a <c>finally</c> and so completes even
/// when the pass is cancelled or throws. A faulted or cancelled pass holds no snapshot open, so the
/// sweep still runs in that case — it is deliberately NOT gated on the pass having SUCCEEDED. Only
/// the host stopping cancels the wait, and then the sweep is skipped rather than run against a
/// directory nobody is left to care about.</para>
/// </summary>
public sealed class StagingSweepService(
    BackupPaths paths,
    TimeProvider timeProvider,
    Func<CancellationToken, Task> waitForFirstMaintenancePass,
    ILogger<StagingSweepService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // arb-07jl: the startup automatic backup must be finished before the instant below is
            // captured, or its in-flight snapshot reads as an orphan. Cancellation and faults are
            // absorbed here rather than propagated: neither leaves a snapshot open, so both mean
            // "nothing of the first pass is still writing" just as a clean completion does.
            await waitForFirstMaintenancePass(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Waiting for the first maintenance pass failed; sweeping anyway.");
        }

        if (stoppingToken.IsCancellationRequested)
        {
            // The host is going down while we waited. Skipping is correct AND safer than sweeping:
            // a shutdown mid-backup is exactly the kill that leaves the orphan the NEXT start
            // reclaims, and that next start has a cut-off that unambiguously post-dates it.
            logger.LogInformation("Staging sweep skipped: the host stopped before the first maintenance pass completed.");
            return;
        }

        try
        {
            // GetUtcNow() at the moment this service is ready to sweep stands in for "process
            // start": it is captured once, here, rather than re-read per file, so every file in
            // the directory is judged against the same instant regardless of how long the
            // enumeration takes.
            StagingSweep.Run(paths.StagingDirectory, timeProvider.GetUtcNow().UtcDateTime, logger);
        }
        catch (Exception ex)
        {
            // Non-invariant startup work (docs/standards/architecture.md): an orphan left in place
            // for one more run is a much smaller problem than the host refusing to start.
            logger.LogError(ex, "Staging sweep failed at startup; will retry on the next restart.");
        }
    }
}
