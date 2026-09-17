using Arbitarr.Data.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// arb-j6vk — THE SHARED PAGING LOOP, lifted out for new absence sweeps rather than copied a third
/// and fourth time.
///
/// <para><b>Why a loop rather than one read at <see cref="LogStore.MaxPageSize"/>.</b> A single page-1
/// read silently bounds every absence assertion built on top of it at 200 rows (arb-j4hq): a host
/// under test starts roughly eight hosted services, so whether the table passes 200 rows is a matter
/// of how much startup chatter the run happens to produce, and a leaked row landing past that boundary
/// is invisible to a scan that never asked for it. That failure mode is the worst kind — the test
/// stays green and stops covering the thing it is named for, with nothing to notice.</para>
///
/// <para><b>Why a loop bounded by <see cref="LogPage.Total"/> rather than an assertion that the total
/// stays under one page.</b> An assertion would convert ordinary startup chatter into a failure that
/// reads as a leak, and it would need re-tuning every time a hosted service gains a log line. The loop
/// here is bounded by <c>Total</c>, which the store computes in the same transaction as the page, so it
/// terminates even while the sink is still appending.</para>
///
/// <para><b>Not a third copy.</b> <see cref="RedirectAccessModeKeyNeverReachesLogsTests"/> and
/// <see cref="ProxyDownloadIndexerKeyNeverLeavesTheProcessTests"/> each already carry an identical
/// private copy of this loop (arb-j4hq) and are deliberately left untouched here — one has an open PR
/// against it. This helper exists for every OTHER absence sweep that needs the same coverage, so the
/// method count does not keep growing by copy-paste.</para>
/// </summary>
internal static class LogStorePaging
{
    /// <summary>
    /// Reads EVERY row in the store, not the first page of them. See the type doc for why a single
    /// page-1 read at <see cref="LogStore.MaxPageSize"/> is not sufficient for an absence sweep.
    ///
    /// <para><b>The caller must flush the sink first.</b> This walks pages in <c>Id DESC</c> order
    /// (see <c>LogStoreTests</c>' paging fact), and a row appended mid-walk shifts that window: a
    /// row that lands ahead of the page already read moves every later row down by one, which can
    /// skip a row entirely rather than merely re-order it. Calling
    /// <c>IServiceCollection.FlushLogSinkAsync</c> (or the equivalent on the host's services) before
    /// this method is what keeps the window stationary for the whole walk.</para>
    /// </summary>
    public static async Task<IReadOnlyList<LogEntry>> ReadAllAsync(WebApplicationFactory<Program> host)
    {
        var store = host.Services.GetRequiredService<LogStore>();
        var entries = new List<LogEntry>();

        for (var page = 1; ; page++)
        {
            var read = await store.ReadAsync(level: null, logger: null, page: page, pageSize: LogStore.MaxPageSize);
            entries.AddRange(read.Entries);

            if (read.Entries.Count == 0 || entries.Count >= read.Total)
            {
                return entries;
            }
        }
    }
}
