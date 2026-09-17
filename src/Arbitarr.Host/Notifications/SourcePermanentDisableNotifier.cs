using System.Collections.Concurrent;
using Arbitarr.Core.Notifications;
using Arbitarr.Data.Notifications;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Host.Notifications;

/// <summary>
/// Which edge of a source's permanent-disable state was just crossed (arb-rx1f).
/// </summary>
public enum SourcePermanentDisableTransition
{
    /// <summary>The source was callable and is now permanently disabled (false -> true).</summary>
    Appeared,

    /// <summary>The source was permanently disabled and is now callable again (true -> false).</summary>
    Cleared,
}

/// <summary>
/// arb-rx1f: delivers ONE notification when a source becomes permanently disabled because upstream
/// rejected Arbitarr's API key, and ONE when a later success clears it. Modelled on
/// <see cref="DownloadRefusalNotifier"/>, which it deliberately mirrors rather than extends — the
/// two conditions are detected at different points and share nothing but the delivery shape.
///
/// <para><b>Why this condition needs its own notifier at all.</b> The permanently-disabled health
/// item on <c>/api/status</c> is PROJECTED FROM THE STORED BACKOFF ROW AT READ TIME, because no
/// clock may expire a rejected key. A read-time projection has no edge: nothing polls it, no tracker
/// holds it, and so there is no equivalent of <c>NotifyingDownloadRefusalTracker</c> for it to
/// be decorated by. The one place the transition is observable is
/// <c>SourceBackoffStore.RecordOutcomeAsync</c>'s caller, which holds the before-state and the
/// after-state together; <c>BudgetedUpstreamSource</c> computes the edge there and calls
/// <see cref="NotifyInBackground"/>.</para>
///
/// <para><b>THE BEFORE-STATE IS READ FROM THE ROW, WHICH IS WHAT MAKES A RESTART SAFE.</b> This type
/// holds no set of "which sources are disabled" and must not grow one. The flag is durable, so a
/// process that starts with a source already disabled reads <c>true</c> BEFORE the next
/// authentication failure and computes no edge — no notification, and no rehydration service needed
/// to arrange that. A private mirror would start empty on every boot and re-announce every already
/// disabled source at the first failed search, which is the duplicate-storm failure the notifier
/// exists to avoid.</para>
///
/// <para><b>It is PUSHED, not polled, for the reason <see cref="DownloadRefusalNotifier"/> sets out
/// at length.</b> Do not route this through <c>NotificationPolicy</c> or
/// <c>NotificationDispatcher</c>: the dispatcher folds stored event rows through a
/// consecutive-failure counter, and a rejected key is not a count of blips — folding it would delay
/// the notice by the threshold and let an unrelated success clear it while the key was still
/// rejected. Everything downstream of the decision is the dispatcher's path unchanged: the same
/// <see cref="NotificationSettings.IsEnabled"/> gate, the same <see cref="WebhookNotificationTransport"/>,
/// the same <c>RecordDeliveryAsync</c>, and the same warning naming the trigger and the outcome —
/// both closed enums — and never the target.</para>
///
/// <para><b>The message is built from the CONFIGURED source name and fixed wording only.</b> Never
/// from the upstream exception, the stored <c>LastOutcome</c>, the source's URL, or its key. The
/// disabling condition is a 401/403 whose body and headers are upstream-supplied and may carry the
/// credential that was rejected, so nothing derived from the failure may reach an operator's
/// notification client, which is a third-party service off this machine.</para>
/// </summary>
public sealed class SourcePermanentDisableNotifier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// One gate PER SOURCE, so the read-record-read triple around
    /// <c>SourceBackoffStore.RecordOutcomeAsync</c> is serialised for that source and two concurrent
    /// authentication failures cannot both observe the pre-record state and both raise the edge.
    ///
    /// <para><b>Per source rather than one global gate, and that is a requirement rather than an
    /// optimisation.</b> <c>UpstreamMergeStage</c> fans out to every configured source under one
    /// <c>Task.WhenAll</c>, and #461 made that fan-out isolate a slow or failing indexer so it cannot
    /// stall the whole search. A single gate held across a SQLite round-trip would put every source's
    /// outcome record behind the slowest one and reintroduce exactly the coupling #461 removed.</para>
    ///
    /// <para><see cref="StringComparer.Ordinal"/> to match the keying
    /// <c>SourceBackoffState.SourceName</c> is stored and looked up under. Reading one source name
    /// through two different comparers is how an edge gets missed in one direction and duplicated in
    /// the other.</para>
    ///
    /// <para>The dictionary is bounded by the number of configured sources — display names come from
    /// rows, not from upstream — so it does not grow per call and needs no eviction.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public SourcePermanentDisableNotifier(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<SourcePermanentDisableNotifier>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<SourcePermanentDisableNotifier>.Instance;
    }

    /// <summary>
    /// The message an operator reads, for one edge and one source. Public so a test asserts the exact
    /// wording that is delivered rather than a re-spelling of it — the same reason
    /// <see cref="DownloadRefusalNotifier.Summarize"/> is public.
    ///
    /// <para>The appeared text names the remediation ("until the key is corrected") because the
    /// condition does not clear on its own: an operator told only that a source is disabled has to go
    /// and find out why, and the why is always the same one thing.</para>
    /// </summary>
    public static string Summarize(string sourceName, SourcePermanentDisableTransition transition) =>
        transition == SourcePermanentDisableTransition.Appeared
            ? $"Source '{sourceName}' rejected Arbitarr's API key and is disabled. Searches skip it until the key is corrected."
            : $"Source '{sourceName}' accepted Arbitarr's API key and is active again.";

    private static NotificationTrigger TriggerFor(SourcePermanentDisableTransition transition) =>
        transition == SourcePermanentDisableTransition.Appeared
            ? NotificationTrigger.SourcePermanentlyDisabled
            : NotificationTrigger.SourcePermanentlyDisabledCleared;

    /// <summary>
    /// Records <paramref name="outcome"/> against the durable backoff state and raises ONE
    /// notification if that crossed the permanent-disable edge in either direction. The whole
    /// feature, in the one place both halves of the edge exist.
    ///
    /// <para><b><c>SourceBackoffStore</c> is left a plain state applier.</b> Its signature does not
    /// change and it is given no callback: it is Data and this is Host, and a store that notified
    /// could not be used by any path that legitimately writes state without announcing it. What this
    /// adds is the two reads around the existing write — both inside the SAME gate scope, so both see
    /// one <c>ArbitarrDbContext</c> and the row cannot move between them.</para>
    ///
    /// <para><b>The before-state is read from the ROW, never from memory, and that is what makes a
    /// restart silent.</b> A fresh process that finds the flag already set reads <c>true</c> before
    /// the next authentication failure and computes no edge — so no rehydration service is needed,
    /// and an operator is not re-told at the first failed search about a source they already know is
    /// disabled.</para>
    ///
    /// <para><b>The per-source gate is held across the read-record-read triple and released BEFORE
    /// the notification is raised</b>, exactly as <c>NotifyingDownloadRefusalTracker</c> does and for
    /// the same two reasons: two concurrent authentication failures for one source must not both
    /// observe the pre-record state and both announce it, and a gate must not be held across
    /// anything the app owns.</para>
    /// </summary>
    /// <param name="scopeFactory">
    /// The caller's gate scope factory. Passed in rather than held, because the store it resolves is
    /// SCOPED over a non-thread-safe context and the search fan-out runs every source at once — see
    /// <c>ISourceGateScopeFactory</c>.
    /// </param>
    public async Task RecordAndNotifyAsync(
        string sourceName,
        SourceCallOutcome outcome,
        ISourceGateScopeFactory scopeFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var gate = _gates.GetOrAdd(sourceName, static _ => new SemaphoreSlim(1, 1));
        SourcePermanentDisableTransition? transition;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            transition = await scopeFactory.UseAsync(
                async (sourceGate, ct) =>
                {
                    // BEFORE, from the row. A null row is a source with no recorded state, which is
                    // not disabled — the same reading IsCallableAsync gives it.
                    var before = await sourceGate.Backoff.GetAsync(sourceName, ct).ConfigureAwait(false);
                    var wasDisabled = before?.IsPermanentlyDisabled ?? false;

                    var after = await sourceGate.Backoff
                        .RecordOutcomeAsync(sourceName, outcome, ct)
                        .ConfigureAwait(false);

                    // A null row AFTER means NotAttempted against a source that had none: nothing was
                    // written, so nothing transitioned.
                    var isDisabled = after?.IsPermanentlyDisabled ?? false;

                    return (wasDisabled, isDisabled) switch
                    {
                        (false, true) => SourcePermanentDisableTransition.Appeared,
                        (true, false) => SourcePermanentDisableTransition.Cleared,
                        // Unchanged in either direction: a repeat authentication failure while
                        // already disabled, a transient failure, a NotAttempted, or a success on a
                        // source that was never disabled. The silence is the requirement rather than
                        // an omission — either the operator has already been told, or there is
                        // nothing to tell them.
                        _ => (SourcePermanentDisableTransition?)null,
                    };
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        if (transition is { } edge)
        {
            NotifyInBackground(sourceName, edge);
        }
    }

    /// <summary>
    /// Delivers the notice for one transition. Awaitable so tests drive it deterministically rather
    /// than racing a background task, exactly as <see cref="DownloadRefusalNotifier.NotifyAsync"/> is.
    ///
    /// <para>Takes its own DI scope: the repositories wrap the scoped <c>ArbitarrDbContext</c>, and
    /// the caller is a source on the search fan-out whose gate scope is disposed the moment the
    /// outcome is recorded.</para>
    /// </summary>
    public async Task NotifyAsync(
        string sourceName,
        SourcePermanentDisableTransition transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        var transport = scope.ServiceProvider.GetRequiredService<WebhookNotificationTransport>();

        var settings = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var trigger = TriggerFor(transition);
        if (!settings.IsEnabled(trigger))
        {
            // Muted, or the notifier is off entirely. Nothing is queued for later: there is no cursor
            // to hold a position with, and the edge has already been consumed by the row write, so a
            // deferred send would have to re-derive a transition that no longer exists.
            return;
        }

        var payload = new NotificationPayload(
            trigger,
            Summarize(sourceName, transition),
            sourceName,
            _timeProvider.GetUtcNow());

        var url = await repository.ReadWebhookUrlForDeliveryAsync(cancellationToken).ConfigureAwait(false);
        var outcome = await transport.DeliverAsync(url, payload, cancellationToken).ConfigureAwait(false);
        await repository.RecordDeliveryAsync(outcome, cancellationToken).ConfigureAwait(false);

        if (outcome != NotificationDeliveryOutcome.Delivered)
        {
            // The TRIGGER and the OUTCOME, never the target — identical to the dispatcher's warning
            // and for the identical reason: both are closed enums, and since #65 log rows are
            // persistent and readable from the System page, so a URL or a source name here would be a
            // durable leak.
            _logger.LogWarning(
                "Notification for {Trigger} was not delivered ({Outcome}); the search path was unaffected.",
                payload.Trigger,
                outcome);
        }
    }

    /// <summary>
    /// The fire-and-forget entry point the gate decorator calls. Returns immediately: the caller is
    /// <c>BudgetedUpstreamSource</c> on the SEARCH path, inside the <c>Task.WhenAll</c> fan-out that
    /// #461 made resilient to one slow source. Awaiting a webhook POST there would hand every search
    /// the notification endpoint's latency and could fail a search because a webhook is
    /// misconfigured, which is the coupling §3.4/AC6 forbids.
    ///
    /// <para><see cref="CancellationToken.None"/> deliberately: the notice describes a state change
    /// that is already durably written, and it must outlive a search whose client disconnects or
    /// whose per-source timeout elapses.</para>
    /// </summary>
    public void NotifyInBackground(string sourceName, SourcePermanentDisableTransition transition)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await NotifyAsync(sourceName, transition, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Per-item error handling (docs/standards/architecture.md). The exception is logged
                // with its trigger and NOT with the source, matching the delivery warning above; the
                // search that produced it has already been answered from the other sources.
                _logger.LogError(ex, "Source permanent-disable notification for {Trigger} failed.", TriggerFor(transition));
            }
        });
    }
}
