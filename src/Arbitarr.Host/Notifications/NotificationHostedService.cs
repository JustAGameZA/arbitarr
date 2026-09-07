using Arbitarr.Core.Notifications;
using Arbitarr.Data.Events;
using Arbitarr.Data.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Host.Notifications;

/// <summary>
/// Schedules <see cref="NotificationDispatcher.RunCycleAsync"/> on a fixed interval (#57 plan §4
/// item 2). Shaped exactly like <see cref="Maintenance.MaintenanceHostedService"/>: a fresh DI
/// scope per run so the dispatcher gets its own scoped <c>ArbitarrDbContext</c> rather than one
/// held open for the process lifetime, and a failed cycle is logged and retried rather than
/// allowed to fault the host.
///
/// <para><b>Why the interval is a constant rather than a setting.</b> Every other tunable in #57 is
/// operator-facing because it changes WHAT they are told; this one only changes how promptly, and
/// the policy's own thresholds already govern that far more meaningfully — a source is not reported
/// until it has failed N times regardless of how often this loop runs. Adding a
/// <c>SettingKey</c> for it would put a knob in the settings UI that no operator has a reason to
/// turn, and <c>SettingsCatalog</c> is deliberately a small, curated list.</para>
///
/// <para><b>A failed cycle costs notifications, never the pipeline.</b> This service reads an event
/// store and posts to a webhook; nothing in the search path waits on it, which is AC6 ("delivery
/// failure never blocks or slows the search pipeline") holding structurally rather than by
/// timeout — the notifier is not on that path at all.</para>
/// </summary>
public sealed class NotificationHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<NotificationHostedService>? logger = null)
    : BackgroundService
{
    /// <summary>
    /// How often the notifier evaluates. A minute is well below any threshold an operator would
    /// set, so it never becomes the reason a notification is late, and far above anything that
    /// would make repeated reads of a small SQLite table a cost worth counting.
    /// </summary>
    public static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = logger ?? NullLogger<NotificationHostedService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed notification cycle must never take the host down: BackgroundService
                // faults propagate to the host by default. Log and retry on the next tick.
                _logger.LogError(ex, "Notification cycle failed; will retry next cycle.");
            }

            try
            {
                await Task.Delay(CycleInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        var dispatcher = new NotificationDispatcher(
            provider.GetRequiredService<EventRepository>(),
            provider.GetRequiredService<NotificationRepository>(),
            provider.GetRequiredService<WebhookNotificationTransport>(),
            timeProvider,
            provider.GetRequiredService<ILogger<NotificationDispatcher>>());

        await dispatcher.RunCycleAsync(cancellationToken).ConfigureAwait(false);
    }
}
