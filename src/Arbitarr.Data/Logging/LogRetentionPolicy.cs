namespace Arbitarr.Data.Logging;

/// <summary>
/// The single named place holding how long application log rows are retained, following the
/// <see cref="Arbitarr.Data.Events.EventRetentionPolicy"/> precedent (#65 plan §4 item 4: "state the
/// figure in one named place"). Do not scatter this figure as a literal elsewhere; look it up here.
///
/// Seven days matches Sonarr's <c>LogRepository.Trim()</c> (<c>DateTime.UtcNow.AddDays(-7)</c>), and
/// deliberately equals <see cref="Arbitarr.Data.Events.EventRetentionPolicy.OperationalRetention"/>
/// rather than being tuned independently: application log rows are the same kind of high-volume,
/// low-value-after-a-week operational record, and an operator reasoning about "how far back can I
/// look" should not have to hold two different answers for two operational surfaces.
///
/// THE VACUUM IS HALF THE POLICY, and it is the half a future reader is most likely to drop as
/// redundant — don't. SQLite does not return freed pages to the filesystem on <c>DELETE</c> alone,
/// so a trim without a vacuum leaves <c>arbitarr-logs.db</c> sitting at its all-time high-water
/// mark forever. On a homelab box with a small config volume that is a slow-motion disk-full
/// outage in which the trim appears to be working the whole time, which is exactly the failure
/// #65 was filed about. <see cref="LogStore.TrimAsync"/> therefore vacuums in the same pass, and
/// <c>LogStoreTests</c> asserts the file actually shrinks rather than merely that rows disappeared.
/// </summary>
public static class LogRetentionPolicy
{
    /// <summary>How long a log row is kept before <see cref="LogStore.TrimAsync"/> removes it.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
}
