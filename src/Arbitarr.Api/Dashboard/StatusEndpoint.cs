using Arbitarr.Api.Routing;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data.CircuitBreaker;
using Arbitarr.Data.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Dashboard;

/// <summary>Per-source health, as reported by <c>/api/status</c>.</summary>
/// <param name="SourceName">Name of the upstream source.</param>
/// <param name="State">Circuit breaker state: "closed", "open", or "half-open".</param>
/// <param name="ConsecutiveFailures">Consecutive failure count since the breaker last closed.</param>
/// <param name="LastError">Most recent error message, if any.</param>
public sealed record SourceStatus(string SourceName, string State, int ConsecutiveFailures, string? LastError);

/// <summary>
/// Proactive-refresh worker health, as reported by <c>/api/status</c> (M7-7, R20) — a direct
/// projection of <see cref="RefreshWorkerHealth"/>, the real snapshot the worker itself maintains.
/// Replaces the pre-M3 <c>WorkerStatus: "not-implemented"</c> placeholder now that a worker exists.
/// </summary>
/// <param name="Enabled">Whether proactive refresh is turned on.</param>
/// <param name="LastCycleStartedUtc">When the most recent cycle began, or null if no cycle has run yet.</param>
/// <param name="LastCycleCompletedUtc">When the most recent cycle finished, or null if none has completed yet.</param>
/// <param name="LastCycleCandidates">How many refresh candidates the most recent cycle selected.</param>
/// <param name="LastCycleRefreshed">How many of those candidates were successfully refreshed.</param>
/// <param name="LastCycleFailed">How many of those candidates failed to refresh.</param>
/// <param name="LastError">The most recent cycle-level failure's message, or null.</param>
/// <param name="ConsecutiveFailedCycles">How many cycles have faulted in a row.</param>
public sealed record WorkerHealthResponse(
    bool Enabled,
    DateTimeOffset? LastCycleStartedUtc,
    DateTimeOffset? LastCycleCompletedUtc,
    int LastCycleCandidates,
    int LastCycleRefreshed,
    int LastCycleFailed,
    string? LastError,
    int ConsecutiveFailedCycles);

/// <summary>
/// One outstanding operator-actionable condition, as reported by <c>/api/status</c>'s health block
/// (arb-ln0).
///
/// Health items are cleared by the specific event that proves the condition is over — for a refused
/// download, an actual successful grab from that source — and by nothing else.
///
/// arb-v3w: they are also <b>persisted</b>, and rehydrated at startup, so they now SURVIVE A
/// RESTART. <see cref="ObservedSinceUtc"/> therefore means "when the condition began", not "first
/// observed since this process started" — which is why the payload carries an absolute instant
/// rather than a duration. Persisting it is what makes that instant meaningful: the NZBHydra2
/// misconfiguration behind a refused redirect outlives the process, so a restart that reset this to
/// "now" (or dropped the item entirely) reported a fresh, clean system while every download still
/// failed.
///
/// Nothing secret-shaped belongs here: <c>/api/status</c> is <c>RouteClassification.PublicRead</c>
/// and un-gated, so <see cref="Summary"/> is built from configured names and status codes only,
/// never from upstream-supplied text.
/// </summary>
/// <param name="Key">Stable machine-readable identifier for the kind of condition, e.g. "download-refused-redirect".</param>
/// <param name="Severity">How bad it is. "blocking" — the only value at present — means the affected function cannot work at all until an operator acts.</param>
/// <param name="SourceName">The configured source the condition applies to.</param>
/// <param name="Summary">Human-readable description of the condition, safe for an un-gated surface.</param>
/// <param name="ObservedSinceUtc">When the condition was first observed. Persisted, so it survives a restart (arb-v3w).</param>
/// <param name="LastObservedUtc">When the condition was most recently observed.</param>
public sealed record HealthItem(
    string Key,
    string Severity,
    string SourceName,
    string Summary,
    DateTimeOffset ObservedSinceUtc,
    DateTimeOffset LastObservedUtc);

/// <summary>Overall service status, as reported by <c>/api/status</c>.</summary>
/// <param name="Status">Overall service status, always "ok" at this milestone (the process is up).</param>
/// <param name="Sources">Per-source health.</param>
/// <param name="Worker">Proactive-refresh worker health (M7-7, R20).</param>
/// <param name="Health">Outstanding operator-actionable conditions (arb-ln0); empty when there are none.</param>
public sealed record StatusResponse(
    string Status,
    IReadOnlyList<SourceStatus> Sources,
    WorkerHealthResponse Worker,
    IReadOnlyList<HealthItem> Health);

/// <summary>Maps the read-only <c>GET /api/status</c> endpoint (M2 §2, D1 surface 1).</summary>
public static class StatusEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/status", HandleAsync)
            .WithClassification(RouteClassification.PublicRead);

    /// <summary>
    /// The <see cref="HealthItem.Key"/> a refused-redirect download reports under. Stable so a
    /// client can branch on the kind of condition without parsing the summary prose.
    ///
    /// <para>Public (arb-d3kt): <c>Arbitarr.Api</c> has no <c>InternalsVisibleTo</c> for the test
    /// assemblies, so an <c>internal</c> modifier here could not actually back the "stable
    /// contract" claim above — tests exercising it had to hardcode the literal instead, which is
    /// precisely the kind of drift a named constant exists to prevent. Making it public is the
    /// smaller fix: the value already crosses the wire on every <c>/api/status</c> response, so
    /// widening its C# accessibility exposes no new information.</para>
    /// </summary>
    public const string DownloadRefusedRedirectKey = "download-refused-redirect";

    /// <summary>
    /// arb-x7w8.11: the <see cref="HealthItem.Key"/> a PERMANENTLY DISABLED indexer reports under.
    /// Public for the same reason <see cref="DownloadRefusedRedirectKey"/> is: the value already
    /// crosses the wire, and an <c>internal</c> one could not back the stable-contract claim from a
    /// test assembly <c>Arbitarr.Api</c> has no <c>InternalsVisibleTo</c> for.
    ///
    /// <para><b>Only the PERMANENT disable is a health item, and that is the bead's whole point.</b>
    /// A health item names a condition the OPERATOR must fix (CONTEXT.md "Health item"); "blocking"
    /// means the affected function cannot work at all until someone acts. An authentication failure
    /// qualifies exactly — no amount of waiting fixes a rejected key, and
    /// <c>SourceBackoffState.IsPermanentlyDisabled</c>'s doc states that clearing it "is an operator
    /// action, never an automatic one". A TRANSIENT BACKOFF DOES NOT QUALIFY and must never be
    /// raised here: it clears itself within minutes, so an item for it would be noise an operator
    /// learns to ignore — which is how a real one stops being read. A BUDGETED source likewise
    /// clears itself when the rolling window moves. Both of those are per-source display state on
    /// the admin-gated sources surface, and neither belongs on this route.</para>
    ///
    /// <para><b>What clears it is the proving event, never elapsed time.</b> The item is a PROJECTION
    /// of the row rather than a separately tracked condition, so it is present for exactly as long as
    /// <c>IsPermanentlyDisabled</c> is set — and the only thing that resets that flag is
    /// <c>SourceBackoffStore.RecordOutcomeAsync</c> observing a genuine
    /// <c>SourceCallOutcome.Success</c> from that source. Nothing decays it, no worker cycle touches
    /// it, and a successful search against a different source does not. Deriving it from the row
    /// rather than mirroring it into a second tracker is what makes that true by CONSTRUCTION: there
    /// is no clock anywhere in this path that could be made to expire it, which is the defect
    /// ADR 0016 and <c>IDownloadRefusalTracker</c> exist to prevent.</para>
    ///
    /// <para><b>The operator action that clears it is re-entering the credential.</b> Saving a
    /// corrected API key through <c>PUT /api/admin/sources/{id}</c> does not itself clear the flag —
    /// deliberately, because an edit is a claim and not yet evidence. It clears on the next call that
    /// actually succeeds with the new key, which is the proving event in exactly the
    /// <c>IDownloadRefusalTracker</c> sense. NO re-enable endpoint is added for it: a button that
    /// cleared the flag directly would assert the condition was over without demonstrating it,
    /// re-enabling a source whose key is still rejected and re-presenting a bad credential to the
    /// indexer — the precise cost <c>SourceBackoffState.IsPermanentlyDisabled</c>'s doc gives for not
    /// escalating auth failures. The existing per-source Test button already lets an operator confirm
    /// a replacement key before anything else touches the indexer.</para>
    ///
    /// <para><b>The summary is built from the CONFIGURED SOURCE NAME and this fixed wording alone.</b>
    /// <c>/api/status</c> is un-gated (see <see cref="HealthItem"/>), so nothing upstream-supplied may
    /// reach it. <c>LastOutcome</c>, the backoff level, <c>DisabledUntil</c> and the budget tallies
    /// are all deliberately absent from this route and live only behind the admin gate. The NAME is
    /// carryable only because this response already publishes it as <c>SourceStatus.SourceName</c>,
    /// a premise <see cref="PermanentlyDisabledItemsAsync"/> enforces rather than assumes by keeping
    /// only states whose name is in that published set (sec-511).</para>
    ///
    /// <para><b>THIS ITEM NOTIFIES, BUT NOT FROM HERE — and nothing on this path should be changed
    /// to make it.</b> CONTEXT.md's "Health item" entry records that items notify since arb-apj
    /// (#247), pushed from a decorator around the tracker that owns them. This one has no such
    /// decorator to hang off because it has no tracker and no edge HERE: it is PROJECTED at read
    /// time from the backoff row, so nothing in this path observes the transition from absent to
    /// present. That remains the projection's design, and it is what makes the item impossible to
    /// expire by elapsed time (see the paragraph above). arb-rx1f supplied the edge at the other
    /// end instead: <c>SourcePermanentDisableNotifier</c> reads the row's flag before and after
    /// <c>SourceBackoffStore.RecordOutcomeAsync</c> — the one point holding both states — and raises
    /// one notification when a source becomes permanently disabled and one when it clears, outside
    /// <c>NotificationPolicy</c>. So do NOT add a tracker, an edge, or a notification call to this
    /// read path to "fix" a gap that is already closed: mirroring the row into a second tracker
    /// would reintroduce exactly the drift the projection removes.</para>
    /// </summary>
    public const string SourcePermanentlyDisabledKey = "source-permanently-disabled";

    private static async Task<StatusResponse> HandleAsync(
        SourceHealthRepository healthRepository,
        IRefreshWorkerHealth workerHealth,
        IDownloadRefusalTracker refusalTracker,
        SourceRepository sourceRepository,
        SourceBackoffStore backoffStore,
        CancellationToken cancellationToken)
    {
        var snapshots = await healthRepository.LoadAllAsync(cancellationToken);

        var sources = snapshots
            .Select(kvp => new SourceStatus(
                SourceName: kvp.Key,
                State: ToStateLabel(kvp.Value.State),
                ConsecutiveFailures: kvp.Value.ConsecutiveFailures,
                LastError: kvp.Value.LastError))
            .OrderBy(s => s.SourceName, StringComparer.Ordinal)
            .ToArray();

        var health = workerHealth.Snapshot;
        var worker = new WorkerHealthResponse(
            Enabled: health.Enabled,
            LastCycleStartedUtc: health.LastCycleStartedUtc,
            LastCycleCompletedUtc: health.LastCycleCompletedUtc,
            LastCycleCandidates: health.LastCycleCandidates,
            LastCycleRefreshed: health.LastCycleRefreshed,
            LastCycleFailed: health.LastCycleFailed,
            LastError: health.LastError,
            ConsecutiveFailedCycles: health.ConsecutiveFailedCycles);

        // arb-ln0: the tracker's snapshot is already ordered and already empty when nothing is
        // refused, so this projects it one-for-one rather than filtering. Severity is "blocking"
        // because every download from that source fails until the operator changes the setting —
        // the source itself stays healthy, so nothing in the Sources block above says so.
        var healthItems = refusalTracker.Snapshot()
            .Select(refusal => new HealthItem(
                Key: DownloadRefusedRedirectKey,
                Severity: "blocking",
                SourceName: refusal.SourceName,
                Summary: refusal.Reason,
                ObservedSinceUtc: refusal.ObservedSinceUtc,
                LastObservedUtc: refusal.LastObservedUtc))
            .Concat(await PermanentlyDisabledItemsAsync(
                sourceRepository,
                backoffStore,
                sources.Select(s => s.SourceName).ToHashSet(StringComparer.Ordinal),
                cancellationToken))
            .ToArray();

        return new StatusResponse(Status: "ok", Sources: sources, Worker: worker, Health: healthItems);
    }

    /// <summary>
    /// arb-x7w8.11: one blocking health item per CONFIGURED source whose backoff row is permanently
    /// disabled. See <see cref="SourcePermanentlyDisabledKey"/> for why only this state qualifies and
    /// what clears it.
    ///
    /// <para><b>Joined against the configured sources rather than projected from the rows alone.</b>
    /// A row survives the source being deleted or renamed — the table is keyed by display name and
    /// has no foreign key to <c>Source</c> and deliberately no time-based prune — so projecting rows
    /// directly would raise a blocking item naming an indexer the operator no longer has, with no
    /// affordance anywhere to clear it. Only a source that still exists can be acted on, so only one
    /// of those is reported.</para>
    ///
    /// <para><b>Disabled sources are reported too, and that is deliberate.</b> An operator who turned
    /// an indexer off has not fixed its credential, and re-enabling it would put the same rejected
    /// key straight back on the wire. Suppressing the item would hide the condition behind an
    /// unrelated toggle.</para>
    ///
    /// <para>Both timestamps come from <c>UpdatedAt</c>, the one instant the row records. The flag
    /// bypasses escalation entirely, so it is set once by the auth failure and not re-stamped while
    /// it persists: "since" and "last observed" are genuinely the same moment here, unlike a refusal
    /// which is re-observed on every attempted download. Fabricating a later "last observed" from the
    /// clock would be the elapsed-time coupling this whole item is built to avoid.</para>
    ///
    /// <para><b>An item may name only a source ALREADY NAMED on this route</b> (sec-511), which is
    /// what <paramref name="publishedSourceNames"/> enforces. Being configured is not sufficient:
    /// the backoff table is keyed by display name with no foreign key, so after a source is renamed
    /// and a NEW source takes the freed name, the join on display name matches the new source
    /// against the old one's row. That would publish the new source's name on this un-gated route
    /// for the first time, and blame it for a credential failure that was never its own. Filtering
    /// to the names <c>SourceStatus</c> already carries makes the item incapable of disclosing a
    /// name the response did not already contain, whatever the join does.</para>
    /// </summary>
    /// <param name="publishedSourceNames">
    /// The source names this response already publishes, from the <c>SourceStatus</c> list. Ordinal,
    /// matching how the backoff store and the caps store compare the same key: a case-only rename is
    /// genuinely a different source here.
    /// </param>
    private static async Task<IEnumerable<HealthItem>> PermanentlyDisabledItemsAsync(
        SourceRepository sourceRepository,
        SourceBackoffStore backoffStore,
        IReadOnlySet<string> publishedSourceNames,
        CancellationToken cancellationToken)
    {
        var states = await backoffStore.GetAllAsync(cancellationToken);

        if (states.Count == 0)
        {
            // No source has ever recorded an outcome, so none can be permanently disabled and the
            // configured sources need not be read. The overwhelmingly common case on a route the
            // dashboard polls.
            return Array.Empty<HealthItem>();
        }

        var sources = await sourceRepository.GetAllAsync(cancellationToken);

        return sources
            .Select(source => states.TryGetValue(source.DisplayName, out var state) ? state : null)
            .Where(state => state is { IsPermanentlyDisabled: true })
            // sec-511: and only if this response already names it. See the filter's rationale above.
            .Where(state => publishedSourceNames.Contains(state!.SourceName))
            .Select(state => new HealthItem(
                Key: SourcePermanentlyDisabledKey,
                Severity: "blocking",
                SourceName: state!.SourceName,
                // FIXED WORDING plus the configured name, and nothing else. No LastOutcome, no
                // level, no upstream text — this route is un-gated (see HealthItem's doc), and the
                // detail lives behind the admin gate on GET /api/admin/sources.
                Summary: $"{state.SourceName} is disabled: it rejected Arbitarr's API key. Searches skip it until the key is corrected.",
                ObservedSinceUtc: state.UpdatedAt,
                LastObservedUtc: state.UpdatedAt))
            .OrderBy(item => item.SourceName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ToStateLabel(CircuitState state) => state switch
    {
        CircuitState.Closed => "closed",
        CircuitState.Open => "open",
        CircuitState.HalfOpen => "half-open",
        _ => throw new InvalidOperationException($"Unknown circuit breaker state: {state}"),
    };
}
