using Arbitarr.Core.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Arbitarr.Host.Diagnostics;

/// <summary>
/// arb-v3w: loads the persisted download refusals into the singleton tracker once, at startup, so a
/// fresh process reports its health items immediately rather than only after the next download
/// happens to fail.
///
/// <para><b>One-shot, not a recurring timer</b> — after this pass the in-memory tracker is the only
/// writer of that table, so there is never anything new to load while this process is running.
/// <c>StartAsync</c> runs the pass and returns; it does not loop, and <c>StopAsync</c> is a no-op.</para>
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
/// <para><b>Ordering: this completes BEFORE the host serves anything.</b> It is a plain
/// <see cref="IHostedService"/>, not a <c>BackgroundService</c>, precisely so that the host awaits
/// <see cref="StartAsync"/> before Kestrel accepts a request. A <c>BackgroundService</c> would not
/// be awaited, leaving a window in which <c>/api/status</c> could answer with only the items loaded
/// so far — a restart would then briefly report a clean dashboard, which is the exact defect this
/// feature exists to close. Because the window is gone, the integration test asserts immediately
/// rather than polling; a poll would hide a regression that reopened it.</para>
///
/// <para>The table this reads is guaranteed to exist by then: <c>Program.cs</c> runs
/// <c>dbContext.Database.Migrate()</c> in the pre-<c>app.Run()</c> startup block, well before
/// <c>app.Run()</c> itself starts the hosted services. Cited by name, not by line: the line
/// numbers drift as the file changes and a stale cite is worse than none.</para>
///
/// <para><b>Rehydration replays through the INNER tracker, not the notifying decorator</b>, so
/// restoring a persisted refusal raises no "appeared" edge. That is the intended behaviour: the
/// condition did not just begin, it merely outlived the process, and an operator must not be paged
/// again for it on every restart.</para>
/// </summary>
public sealed class DownloadRefusalRehydrationService(
    PersistentDownloadRefusalTracker tracker,
    ILogger<DownloadRefusalRehydrationService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await tracker.RehydrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down before the pass finished. Not a fault.
        }
        catch (Exception ex)
        {
            // Swallowed on purpose, and this is what keeps the awaited StartAsync safe: a store
            // failure degrades to the pre-arb-v3w behaviour instead of preventing the host starting.
            logger.LogWarning(
                ex,
                "Rehydrating persisted download-refusal health items failed; any outstanding refusal will reappear on the next refused download.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
