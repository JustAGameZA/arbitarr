using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Notifications;
using Arbitarr.Data.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Host.Notifications;

/// <summary>
/// arb-apj: delivers ONE notification when a source's redirect-refusal health item appears and ONE
/// when it clears. The edges themselves are detected by
/// <see cref="NotifyingDownloadRefusalTracker"/>; this type is only the delivery half, and it lives
/// in Arbitarr.Host for the reason <see cref="NotificationDispatcher"/> states — the Host is the
/// sole composition root and the only project permitted to reference both Core and Data.
///
/// <para><b>It is PUSHED, not polled, and that is the one place it departs from
/// <see cref="NotificationDispatcher"/>.</b> The dispatcher polls the event store because its
/// signals are stored rows it must fold in cursor order across restarts. A refusal transition is
/// not a stored row at all: the refusal's event row is deliberately written with a NULL source name
/// (see <c>DownloadProxyEndpoint</c>'s catch block, which explains that naming it would feed the
/// consecutive-failure counter and announce a healthy source as down), so there is nothing on the
/// polled path that could identify the source, and the CLEARING edge writes no row whatsoever. The
/// transition is observable only where it happens. Do not try to route this through the dispatcher
/// — doing so would require giving the event row a source name, which is the bug arb-ln0 avoided.
/// The cost is that a transition is not durable across a restart, which matches the tracker itself:
/// its state is process-lifetime by design, so a restart has no prior state to transition FROM.</para>
///
/// <para><b>Everything else is the dispatcher's path unchanged</b>: the same
/// <see cref="NotificationSettings.IsEnabled"/> gate, the same
/// <see cref="WebhookNotificationTransport"/>, the same <c>RecordDeliveryAsync</c>, and the same
/// warning that names the trigger and the outcome — both closed enums — and never the target. So a
/// refusal notice is muted, delivered, classified and logged exactly as the other four are, and the
/// webhook URL cannot reach a log row through this path either.</para>
///
/// <para><b>The message is built from the CONFIGURED source name and fixed wording only.</b> Never
/// from upstream text such as a <c>Location</c> header, and not even from the tracker's own reason
/// string — which is safe by construction today but is built at a call site this type does not own.
/// Keeping the text wholly local means nothing upstream-derived can reach an operator's
/// notification client no matter what a future caller passes to <c>RecordRefusal</c>.</para>
/// </summary>
public sealed class DownloadRefusalNotifier
{
    /// <summary>
    /// The remediation sentence. NZBHydra2's "NZB access type: Redirect to indexer" is the single
    /// setting that produces this condition, so naming it turns a notification the operator can only
    /// act on by searching into one they can act on directly. Fixed text, in the product's own
    /// words — it describes OUR configuration expectation, not anything the upstream said.
    /// </summary>
    public const string Remediation =
        "Set the source's NZB access type to Proxy so it serves the file instead of redirecting.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public DownloadRefusalNotifier(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DownloadRefusalNotifier>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<DownloadRefusalNotifier>.Instance;
    }

    /// <summary>
    /// The message an operator reads, for one edge and one source. Public so a test asserts the
    /// exact wording that is delivered rather than a re-spelling of it.
    /// </summary>
    public static string Summarize(string sourceName, DownloadRefusalTransition transition) =>
        transition == DownloadRefusalTransition.Appeared
            ? $"Source '{sourceName}' refused a download by redirecting instead of serving the file. {Remediation}"
            : $"Source '{sourceName}' is serving downloads again.";

    private static NotificationTrigger TriggerFor(DownloadRefusalTransition transition) =>
        transition == DownloadRefusalTransition.Appeared
            ? NotificationTrigger.DownloadRefused
            : NotificationTrigger.DownloadRefusalCleared;

    /// <summary>
    /// Delivers the notice for one transition. Awaitable so tests drive it deterministically rather
    /// than racing a background task — the same reason <see cref="NotificationDispatcher.RunCycleAsync"/>
    /// is public.
    ///
    /// <para>Takes its own DI scope: the repositories wrap the scoped <c>ArbitarrDbContext</c>, and
    /// the caller here is a request on the download path whose scope may be torn down the moment
    /// the 502 is written.</para>
    /// </summary>
    public async Task NotifyAsync(
        string sourceName,
        DownloadRefusalTransition transition,
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
            // Muted, or the notifier is off entirely. Nothing is queued for later: unlike the
            // dispatcher there is no cursor to hold a position with, and re-sending a transition
            // the operator asked not to hear about is the duplicate-storm failure §3.1 names.
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
            // persistent and readable from the System page, so a URL here would be a durable leak.
            _logger.LogWarning(
                "Notification for {Trigger} was not delivered ({Outcome}); the download path was unaffected.",
                payload.Trigger,
                outcome);
        }
    }

    /// <summary>
    /// The fire-and-forget entry point the tracker decorator calls. Returns immediately: the caller
    /// is <c>DownloadProxyEndpoint</c>'s catch block on a live request, and §3.4/AC6 forbid a
    /// notification delaying or failing the operation that produced it. Every failure is caught and
    /// logged rather than left to fault an unobserved task.
    ///
    /// <para><see cref="CancellationToken.None"/> deliberately, exactly as the refusal's event write
    /// beside it: the notice describes a request that has already failed and must outlive a client
    /// that disconnects mid-write.</para>
    /// </summary>
    public void NotifyInBackground(string sourceName, DownloadRefusalTransition transition)
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
                // with its trigger; the download it followed has already been answered.
                _logger.LogError(ex, "Download-refusal notification for {Trigger} failed.", TriggerFor(transition));
            }
        });
    }
}
