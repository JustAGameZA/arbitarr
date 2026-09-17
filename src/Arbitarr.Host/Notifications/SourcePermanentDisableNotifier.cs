using System.Collections.Concurrent;
using Arbitarr.Core.Notifications;
using Arbitarr.Data.Notifications;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Microsoft.Data.Sqlite;
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

    /// <summary>
    /// Guards <see cref="_inFlight"/> and <see cref="_idle"/> together. Both are read and written as
    /// one unit — the decision to complete <see cref="_idle"/> is made from the count — so they
    /// cannot be split across two independent synchronisation primitives without reintroducing the
    /// race that would let a waiter observe zero before the last delivery's continuation ran.
    /// </summary>
    private readonly object _quiescenceGate = new();

    /// <summary>
    /// Deliveries scheduled by <see cref="NotifyInBackground"/> that have not finished yet. Counted
    /// rather than collected: a list of completed tasks would grow for the life of the process, and
    /// nothing here needs the tasks themselves, only whether any remain.
    /// </summary>
    private int _inFlight;

    /// <summary>
    /// Completed when <see cref="_inFlight"/> next reaches zero, then replaced. Null while nothing is
    /// in flight, which is what lets <see cref="DeliveriesIdle"/> answer without allocating in the
    /// common case.
    ///
    /// <para><see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> is required, not
    /// stylistic: this source is completed from the last delivery's continuation, so a waiter resumed
    /// inline would run ON that delivery's thread — in a test, that means assertions executing inside
    /// the notifier's own bookkeeping.</para>
    /// </summary>
    private TaskCompletionSource? _idle;

    /// <summary>
    /// A task completing when every delivery raised by <see cref="NotifyInBackground"/> SO FAR has
    /// finished — whether it delivered, failed, or threw. Already completed when nothing is in
    /// flight.
    ///
    /// <para><b>What it is for.</b> Deliveries are fire-and-forget, so there is otherwise nothing to
    /// await and no way to tell "the notice has not been sent yet" from "no notice will be sent".
    /// Tests need that distinction to assert either outcome without a wall-clock sleep standing in
    /// for it, and an orderly shutdown could await this to avoid dropping a notice that is already
    /// in flight.</para>
    ///
    /// <para><b>The search path must NEVER await this.</b> Doing so would hand every search the
    /// webhook endpoint's latency and could fail a search because a notification target is
    /// misconfigured — precisely the coupling <see cref="NotifyInBackground"/> exists to prevent. It
    /// is an observation point, not a synchronisation point for production callers.</para>
    ///
    /// <para>It covers deliveries STARTED BEFORE IT WAS READ. A delivery raised after this returns is
    /// not included, which is why a caller arms it before the act that should produce one.</para>
    ///
    /// <para><c>public</c>, not <c>internal</c>: there is no <c>InternalsVisibleTo</c> from this
    /// project to either test assembly (the same reason as <see cref="ReadAttempts"/> below), and the
    /// seam is useless if the tests that need it cannot reach it.</para>
    /// </summary>
    public Task DeliveriesIdle
    {
        get
        {
            lock (_quiescenceGate)
            {
                return _inFlight == 0
                    ? Task.CompletedTask
                    : (_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }

    /// <summary>
    /// How many times <see cref="ReadWithRetryAsync{T}"/> attempts one settings read before giving
    /// up. Three TOTAL attempts, not three retries after the first.
    ///
    /// <para>The number is small on purpose. This runs on a background task raised from the search
    /// fan-out, and the condition being announced is already durably recorded, so a long retry chain
    /// would buy nothing an operator can act on any sooner while holding a thread-pool thread and a
    /// SQLite connection for the duration.</para>
    ///
    /// <para><c>public</c>, not <c>internal</c>: there is no <c>InternalsVisibleTo</c> from this
    /// project to the test assembly, and the bound is asserted by name so that a test cannot drift
    /// from the value it is meant to be pinning — re-spelling "3" in the test is exactly how such an
    /// assertion silently stops describing the product.</para>
    /// </summary>
    public const int ReadAttempts = 3;

    /// <summary>
    /// The pause between read attempts, taken through <see cref="TimeProvider"/> so a test drives it
    /// without real waiting. Short because the faults this covers are brief by nature: a checkpoint
    /// or a vacuum holding an exclusive lock, not an outage.
    ///
    /// <para><c>public</c> for the same reason as <see cref="ReadAttempts"/>: no
    /// <c>InternalsVisibleTo</c>, and a test that advanced a hand-copied span would stop releasing
    /// the pause the moment this value changed.</para>
    /// </summary>
    public static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(200);

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
    /// <para>The appeared text does not promise a path back to enabled: nothing currently re-enables a
    /// permanently disabled source once its key is corrected (arb-fllv), so the message only states what
    /// happened and that searches skip the source, rather than implying a remediation that would work.</para>
    /// </summary>
    public static string Summarize(string sourceName, SourcePermanentDisableTransition transition) =>
        transition == SourcePermanentDisableTransition.Appeared
            ? $"Source '{sourceName}' rejected Arbitarr's API key and is disabled. Searches skip it."
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

        var trigger = TriggerFor(transition);

        var settings = await ReadWithRetryAsync(
            ct => repository.GetSettingsAsync(ct),
            trigger,
            cancellationToken).ConfigureAwait(false);

        if (!settings.IsEnabled(trigger))
        {
            // Muted, or the notifier is off entirely. Nothing is queued for later: there is no cursor
            // to hold a position with, and the edge has already been consumed by the row write, so a
            // deferred send would have to re-derive a transition that no longer exists. That remains
            // the right answer for THIS case — the operator asked not to be told.
            //
            // It is NOT the right answer for a FAILURE before the POST, which is a different thing
            // wearing the same shape: the operator did want to be told and the notice was lost
            // anyway. Because the disable flag is committed before this task runs and no later
            // search re-raises the edge (see ReadWithRetryAsync's remarks), such a loss is
            // permanent. The two settings reads are therefore retried a bounded number of times.
            // A SUSTAINED fault still loses the notice, and that residual is accepted knowingly.
            //
            // Closing the residual entirely would need a durable "notified" flag written after the
            // POST, and that was REJECTED: a flag written late is a flag that can be missing after a
            // crash or a restart, and this type's whole restart story (see the class remarks) is
            // that state is read from the row so a process which starts with a source already
            // disabled announces nothing. A second flag would reintroduce exactly the duplicate
            // storm at boot that reading the row avoids, in exchange for a narrower fault window.
            return;
        }

        var payload = new NotificationPayload(
            trigger,
            Summarize(sourceName, transition),
            sourceName,
            _timeProvider.GetUtcNow());

        var url = await ReadWithRetryAsync(
            ct => repository.ReadWebhookUrlForDeliveryAsync(ct),
            trigger,
            cancellationToken).ConfigureAwait(false);

        // NOT retried, deliberately. DeliverAsync already bounds itself with its own timeout, and a
        // re-POST cannot be told apart from a first POST by the receiving end — so a retry here
        // would risk telling an operator twice that a source died, which is the duplicate-notice
        // failure this whole type is built to avoid. A failed POST is reported by the warning below
        // and by the recorded delivery outcome.
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
    /// Runs one of <see cref="NotifyAsync"/>'s two settings reads, retrying a transient SQLite fault
    /// up to <see cref="ReadAttempts"/> times in total.
    ///
    /// <para><b>Why the reads are retried when nothing else here is.</b> The permanent-disable flag
    /// is committed by <c>SourceBackoffStore.RecordOutcomeAsync</c> BEFORE this task is raised, and
    /// the two are not one transaction. Once that row says disabled, the edge can never be computed
    /// again — a later authentication failure reads <c>true</c> and yields no transition, and in fact
    /// never gets that far, because <c>IsCallableAsync</c> refuses the source outright so the search
    /// path stops recording outcomes for it at all. Only a success clears the flag, and a refused
    /// source is never called to produce one. So an exception thrown between that commit and the POST
    /// does not delay the notice, it destroys it: the operator is never told the source went dead,
    /// and nothing re-raises it. These two reads are the only app code on that stretch, and both are
    /// idempotent, which is what makes retrying them safe.</para>
    ///
    /// <para><b><see cref="SqliteException"/> as a whole family, not narrowed by result code.</b>
    /// SQLITE_BUSY and SQLITE_LOCKED from a concurrent checkpoint or vacuum are the expected shapes,
    /// but a connection that fails to open and the journal-mode verification in
    /// <c>SqliteConnectionFactory</c> throw the same type for what is, from here, the same situation:
    /// the database was momentarily not answerable. Narrowing by result code would silently drop
    /// those back into the losing path for no benefit, since the response to all of them is identical
    /// and bounded. Anything that is NOT a SQLite fault is left to propagate to the caller's backstop
    /// unretried, because it is not evidence of a transient condition.</para>
    ///
    /// <para>Cancellation is never retried: an <see cref="OperationCanceledException"/> means the
    /// host is going away, and re-reading would only delay that.</para>
    /// </summary>
    private async Task<T> ReadWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> read,
        NotificationTrigger trigger,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (attempt < ReadAttempts)
            {
                // The TRIGGER and the ATTEMPT only. Never the source, the webhook URL or the stored
                // key: since #65 log rows are persistent and readable through GET /api/admin/logs,
                // so anything named here is a durable leak (CLAUDE.md §1). The exception is passed as
                // the log's exception rather than interpolated, for the same reason the backstop does
                // it that way.
                _logger.LogWarning(
                    ex,
                    "Reading notification settings for {Trigger} failed on attempt {Attempt} of {Attempts}; retrying.",
                    trigger,
                    attempt,
                    ReadAttempts);

                await Task.Delay(ReadRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
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
    ///
    /// <para>The task is not returned — the caller must not await it, per the above. It is counted
    /// instead, so <see cref="DeliveriesIdle"/> can report when the work has finished without any
    /// caller gaining the ability to block on it.</para>
    /// </summary>
    public void NotifyInBackground(string sourceName, SourcePermanentDisableTransition transition)
    {
        // Counted BEFORE the task is scheduled, so a caller that reads DeliveriesIdle after this
        // returns can never observe zero for a delivery this call already committed to raising.
        lock (_quiescenceGate)
        {
            _inFlight++;
        }

        _ = Task.Run(async () =>
        {
            // The finally wraps the ENTIRE body rather than sitting inside the catch below: the
            // decrement must happen even for a throw the catch does not see — an exception from
            // TriggerFor while building the log arguments, or an OperationCanceledException, which
            // would otherwise strand the count above zero and hang every future waiter forever.
            try
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
            }
            finally
            {
                TaskCompletionSource? idle = null;

                lock (_quiescenceGate)
                {
                    if (--_inFlight == 0)
                    {
                        // Detached and cleared under the lock, completed outside it. Completing while
                        // holding the gate would run a waiter's continuation with the lock held if the
                        // source were ever created without RunContinuationsAsynchronously, and the
                        // next delivery would then block behind an unrelated test's assertions.
                        idle = _idle;
                        _idle = null;
                    }
                }

                idle?.TrySetResult();
            }
        });
    }
}
