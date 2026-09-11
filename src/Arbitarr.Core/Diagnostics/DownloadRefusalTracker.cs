namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// One source's outstanding download refusal, as surfaced on <c>/api/status</c>'s health block.
/// </summary>
/// <param name="SourceName">The CONFIGURED source name the refusal was recorded against.</param>
/// <param name="Reason">
/// Why the download was refused. Built by the caller from the configured source name and a status
/// code only — never from upstream-supplied text such as a <c>Location</c> header. <c>/api/status</c>
/// is <c>RouteClassification.PublicRead</c>, so anything placed here is un-gated; see
/// <c>DownloadProxyEndpoint</c>'s catch block, which states the same rule for the activity event.
/// </param>
/// <param name="ObservedSinceUtc">When this source first refused, counting from process start.</param>
/// <param name="LastObservedUtc">When this source most recently refused.</param>
public sealed record DownloadRefusal(
    string SourceName,
    string Reason,
    DateTimeOffset ObservedSinceUtc,
    DateTimeOffset LastObservedUtc);

/// <summary>
/// Tracks, per source, that a download was refused because the upstream answered with a redirect
/// (see <c>UpstreamRedirectRefusedException</c> and ADR 0014), so the dashboard can show a STICKY
/// health item rather than only the transient activity event the refusal also records.
/// </summary>
/// <remarks>
/// <para>
/// The refusal is a configuration answer from a healthy upstream: it repeats on every download
/// until the operator changes the setting. That is why an entry clears on exactly ONE event — an
/// actual successful grab from the SAME source. It deliberately does not clear on elapsed time, on
/// a refresh-worker cycle, or on a successful search: none of those demonstrates that the download
/// path works, and clearing on any of them would let the banner disappear while every download
/// still fails. That is precisely the invisibility ADR 0014 records as the original defect.
/// </para>
/// <para>
/// Process-lifetime and in-memory by design at this stage: nothing is persisted (persistence is
/// tracked separately as arb-v3w) and nothing is notified (arb-apj). A restart therefore clears
/// every health item, which the status payload states rather than hides — <see cref="DownloadRefusal.ObservedSinceUtc"/>
/// is "since this process started", not "since the condition began".
/// </para>
/// </remarks>
public interface IDownloadRefusalTracker
{
    /// <summary>
    /// Records that <paramref name="sourceName"/> refused a download at <paramref name="at"/>.
    /// The first call for a source fixes <see cref="DownloadRefusal.ObservedSinceUtc"/>; later calls
    /// advance only <see cref="DownloadRefusal.LastObservedUtc"/> and the reason, so the operator
    /// keeps seeing how long the condition has been running.
    /// </summary>
    void RecordRefusal(string sourceName, string reason, DateTimeOffset at);

    /// <summary>
    /// Clears <paramref name="sourceName"/>'s refusal, if any. Called ONLY from a genuinely
    /// successful download of a payload from that source.
    /// </summary>
    void RecordSuccessfulGrab(string sourceName);

    /// <summary>Every outstanding refusal, ordered by source name. Empty when nothing is refused.</summary>
    IReadOnlyList<DownloadRefusal> Snapshot();
}

/// <summary>
/// Default <see cref="IDownloadRefusalTracker"/>: a thread-safe, in-memory, process-lifetime holder,
/// registered as a singleton in the Host so the download proxy (writer) and the status endpoint
/// (reader) share one instance. Tests construct it directly; it needs no dependencies.
/// </summary>
public sealed class DownloadRefusalTracker : IDownloadRefusalTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DownloadRefusal> _refusals = new(StringComparer.Ordinal);

    public void RecordRefusal(string sourceName, string reason, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(reason);

        lock (_gate)
        {
            // ObservedSinceUtc is preserved across repeats on purpose: a refusal that has been
            // happening for an hour must not read as brand new on every Sonarr retry.
            var observedSince = _refusals.TryGetValue(sourceName, out var existing)
                ? existing.ObservedSinceUtc
                : at;

            _refusals[sourceName] = new DownloadRefusal(sourceName, reason, observedSince, at);
        }
    }

    public void RecordSuccessfulGrab(string sourceName)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        lock (_gate)
        {
            _refusals.Remove(sourceName);
        }
    }

    public IReadOnlyList<DownloadRefusal> Snapshot()
    {
        lock (_gate)
        {
            return _refusals.Values
                .OrderBy(r => r.SourceName, StringComparer.Ordinal)
                .ToArray();
        }
    }
}

/// <summary>
/// No-op tracker: the default wherever the download proxy is exercised without a real tracker
/// (existing tests, other call sites), so the health feature is additive.
/// </summary>
public sealed class NullDownloadRefusalTracker : IDownloadRefusalTracker
{
    public static readonly NullDownloadRefusalTracker Instance = new();

    public void RecordRefusal(string sourceName, string reason, DateTimeOffset at)
    {
    }

    public void RecordSuccessfulGrab(string sourceName)
    {
    }

    public IReadOnlyList<DownloadRefusal> Snapshot() => Array.Empty<DownloadRefusal>();
}
