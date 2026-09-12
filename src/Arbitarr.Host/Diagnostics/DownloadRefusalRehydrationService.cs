using Arbitarr.Core.Diagnostics;
using Arbitarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
///
/// <para><b>arb-pu58: the same pass prunes rows for sources that no longer exist.</b> A refusal row is
/// deleted only on a successful grab, so removing a source while its refusal stands orphans the row —
/// and the operator cannot clear it, because the one clearing event is a grab from the source they
/// just deleted. Replaying it would show a blocking Dashboard item, and since arb-apj notify about,
/// a source that is gone.</para>
///
/// <para><b>Reading the source names here is safe by the same ordering that makes the table safe.</b>
/// <c>Program.cs</c> runs <c>SourceSeeder.SeedAndResolveAsync</c> (line 986) after
/// <c>Database.Migrate()</c> and before <c>app.Run()</c>, so by the time this hosted service starts
/// the <c>Sources</c> table is both migrated and seeded. Reading it any earlier would race the seed
/// and could prune against an empty table on the very first run.</para>
/// </summary>
public sealed class DownloadRefusalRehydrationService(
    PersistentDownloadRefusalTracker tracker,
    IServiceScopeFactory scopeFactory,
    ILogger<DownloadRefusalRehydrationService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // EVERY source row's display name, including disabled ones — not the single source
            // resolved in force. A disabled source may be re-enabled and its refusal is probably
            // still true, while an install with no enabled source resolves to nothing at all; either
            // reading would delete rows that are not orphaned. See
            // IDownloadRefusalStore.PruneUnknownSourcesAsync for the full reasoning.
            List<string> knownSourceNames;
            using (var scope = scopeFactory.CreateScope())
            {
                knownSourceNames = await scope.ServiceProvider
                    .GetRequiredService<ArbitarrDbContext>()
                    .Sources
                    .AsNoTracking()
                    .Select(s => s.DisplayName)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var pruned = await tracker.RehydrateAsync(knownSourceNames, cancellationToken).ConfigureAwait(false);

            if (pruned > 0)
            {
                // A COUNT, never the names. This lands in the persistent log store served at
                // /api/admin/logs (CLAUDE.md §1), and a source display name is operator-supplied text
                // that has no business on that surface merely to report a tidy-up.
                logger.LogInformation(
                    "Discarded {PrunedCount} persisted download-refusal health item(s) for sources that are no longer configured.",
                    pruned);
            }
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
