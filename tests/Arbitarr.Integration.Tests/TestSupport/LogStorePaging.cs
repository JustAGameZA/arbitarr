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
/// <para><b>Not a copy, three times over.</b> <see cref="RedirectAccessModeKeyNeverReachesLogsTests"/>,
/// <see cref="ProxyDownloadIndexerKeyNeverLeavesTheProcessTests"/> and
/// <see cref="ReleaseLookupPayloadSecretTests"/> each carried an identical private copy of this loop
/// (arb-j4hq). arb-ibzb folded all three into this one helper, which is now the single implementation
/// every absence sweep in this project calls for a full-table log read.</para>
/// </summary>
internal static class LogStorePaging
{
    /// <summary>
    /// Flushes the log sink, then reads EVERY row in the store, not the first page of them. See the
    /// type doc for why a single page-1 read at <see cref="LogStore.MaxPageSize"/> is not sufficient
    /// for an absence sweep.
    ///
    /// <para><b>Why this method owns the flush rather than requiring the caller to do it first.</b>
    /// This walks pages in <c>Id DESC</c> order (see <c>LogStoreTests</c>' paging fact), and a row
    /// appended mid-walk shifts that window: a row that lands ahead of the page already read moves
    /// every later row down by one, which can skip a row entirely rather than merely re-order it. A
    /// precondition the caller has to remember is exactly the kind of thing that gets forgotten on a
    /// new call site and still passes green, silently narrowing the sweep it is meant to protect — so
    /// the flush lives here instead, where it cannot be skipped. Callers that already flush
    /// immediately beforehand are unaffected: <c>IServiceProvider.FlushLogSinkAsync</c> is idempotent,
    /// and flushing twice in a row is a no-op the second time.</para>
    ///
    /// <para><b>This throws on a host with no <c>SqliteLoggerProvider</c> registered.</b> That is
    /// intended, not a defect to guard against: every caller here is an absence sweep that only makes
    /// sense against a host with the real log store wired in, so a host missing it is a fixture bug
    /// that should fail loudly rather than have this method silently no-op past it.</para>
    /// </summary>
    public static async Task<IReadOnlyList<LogEntry>> ReadAllAsync(WebApplicationFactory<Program> host)
    {
        await host.Services.FlushLogSinkAsync();

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
