using Arbitarr.Core.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Arbitarr.Host.Diagnostics;

/// <summary>
/// arb-v3w: loads the persisted download refusals into the singleton tracker once, at startup, so a
/// fresh process reports its health items immediately rather than only after the next download
/// happens to fail.
///
/// <para><b>One-shot, not a recurring timer</b> — the same shape as <c>StagingSweepService</c>, and
/// for an analogous reason: after this pass the in-memory tracker is the only writer of that table,
/// so there is never anything new to load while this process is running. <c>ExecuteAsync</c> runs
/// the pass and returns; it does not loop or hold the host's shutdown token.</para>
///
/// <para><b>Why a hosted service rather than a lazy first read.</b> A lazy rehydrate would have to
/// hang off <c>Snapshot()</c>, which is called from <c>/api/status</c> on the request path and is
/// synchronous — so the load would either block a request thread or race two concurrent callers into
/// two loads. Doing it once at startup keeps <c>Snapshot()</c> exactly as cheap as it was.</para>
///
/// <para><b>A failure here does not stop the host.</b> Non-invariant startup work
/// (docs/standards/architecture.md): health items are a diagnostic surface, and degrading to the
/// pre-arb-v3w behaviour — items appear from the next refusal onwards — is a far smaller problem
/// than refusing to start. The rows are not lost; the next refusal upserts over them.</para>
///
/// <para><b>Ordering note, as for StagingSweepService.</b> <c>BackgroundService.ExecuteAsync</c> is
/// not awaited before the host reports started, so Kestrel can serve a <c>/api/status</c> request
/// while this pass is still running; such a request simply sees the items it has loaded so far. That
/// is a narrow, self-correcting window on a read-only diagnostic surface, not a correctness
/// problem — and it is why the integration test polls rather than asserting immediately.</para>
/// </summary>
public sealed class DownloadRefusalRehydrationService(
    PersistentDownloadRefusalTracker tracker,
    ILogger<DownloadRefusalRehydrationService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await tracker.RehydrateAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down before the pass finished. Not a fault.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Rehydrating persisted download-refusal health items failed; any outstanding refusal will reappear on the next refused download.");
        }
    }
}
