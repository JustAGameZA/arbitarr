using System.Text.Json;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Notifications;
using Arbitarr.Data;
using Arbitarr.Data.Notifications;
using Arbitarr.Host.Notifications;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-apj's delivery half: the seam where a refusal TRANSITION becomes a real POST through the
/// same transport, the same trigger gate and the same delivery record as #57's other four notices.
/// <c>Arbitarr.Core.Tests.NotifyingDownloadRefusalTrackerTests</c> pins which edges are raised;
/// this pins what happens to one once it is.
///
/// <para><b>The message is asserted against the SERIALIZED BODY, not against the payload record.</b>
/// The operator reads what the transport posted, so that is what is checked — a test that inspected
/// a <c>NotificationPayload</c> would pass even if serialization dropped the message entirely.</para>
///
/// All webhook URLs are obviously-fake <c>example.com</c> forms.
/// </summary>
public sealed class DownloadRefusalNotifierTests : IDisposable
{
    private const string WebhookUrl = "https://example.com/hooks/placeholder-refusal-token";

    private const string SourceName = "nzbhydra2";

    private readonly SqliteTestDatabase _database = new("arr-searcher-refusal-notifier-test");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingHandler _handler = new();
    private ServiceProvider? _provider;

    public void Dispose()
    {
        _provider?.Dispose();
        _database.Dispose();
    }

    /// <summary>Captures the real posted bodies, so assertions read what the operator would.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    /// <summary>
    /// Builds the notifier over a real scope factory and a real <see cref="NotificationRepository"/>
    /// — the notifier takes its own scope per delivery (the download request's is gone by then), so
    /// a hand-rolled fake factory would not exercise the thing most likely to be wrong.
    /// </summary>
    private async Task<DownloadRefusalNotifier> CreateNotifierAsync(
        bool enabled = true,
        IReadOnlySet<NotificationTrigger>? triggers = null,
        string? webhookUrl = WebhookUrl)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ArbitarrDbContext>(options => options.UseSqlite(_database.ConnectionString));
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped(sp => new NotificationRepository(sp.GetRequiredService<ArbitarrDbContext>(), _time));
        services.AddSingleton(new WebhookNotificationTransport(new HttpClient(_handler)));

        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
            await context.Database.MigrateAsync();

            await scope.ServiceProvider.GetRequiredService<NotificationRepository>().SetSettingsAsync(
                new NotificationSettings(
                    Enabled: enabled,
                    ConsecutiveFailureThreshold: 3,
                    SuppressionRateThreshold: 0.5,
                    SuppressionRateWindow: TimeSpan.FromHours(1),
                    EnabledTriggers: triggers ?? NotificationSettings.AllTriggers),
                webhookUrl,
                CancellationToken.None);
        }

        return new DownloadRefusalNotifier(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _time);
    }

    private static string MessageOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("message").GetString() ?? string.Empty;

    private static string TriggerOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("trigger").GetString() ?? string.Empty;

    [Fact]
    public async Task An_appeared_transition_posts_one_notice_naming_the_source_and_the_remediation()
    {
        var notifier = await CreateNotifierAsync();

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Appeared);

        var body = Assert.Single(_handler.Bodies);
        Assert.Equal("downloadRefused", TriggerOf(body));

        var message = MessageOf(body);
        Assert.Contains(SourceName, message, StringComparison.Ordinal);
        Assert.Contains("redirecting instead of serving the file", message, StringComparison.Ordinal);

        // The remediation sentence is the operator's next action, so it is pinned rather than
        // merely allowed: a notice that says something is broken without saying which setting to
        // change sends them hunting.
        Assert.Contains(DownloadRefusalNotifier.Remediation, message, StringComparison.Ordinal);
        Assert.Contains("Proxy", DownloadRefusalNotifier.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cleared_transition_posts_one_notice_under_the_closing_trigger()
    {
        var notifier = await CreateNotifierAsync();

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Cleared);

        var body = Assert.Single(_handler.Bodies);
        Assert.Equal("downloadRefusalCleared", TriggerOf(body));

        var message = MessageOf(body);
        Assert.Contains(SourceName, message, StringComparison.Ordinal);
        Assert.Contains("serving downloads again", message, StringComparison.Ordinal);

        // The closing notice must NOT repeat the remediation: the thing it asks for has been done.
        Assert.DoesNotContain(DownloadRefusalNotifier.Remediation, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_notice_carries_no_text_that_did_not_originate_here()
    {
        // The summary is built from the CONFIGURED source name and fixed wording only — never from
        // upstream-supplied text such as a Location header, and not even from the tracker's reason
        // string. The source name below is the only variable part, and it is one this product
        // stored, so nothing an upstream can control reaches the operator's notification client.
        var notifier = await CreateNotifierAsync();

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Appeared);

        var body = Assert.Single(_handler.Bodies);
        var message = MessageOf(body);

        Assert.Equal(DownloadRefusalNotifier.Summarize(SourceName, DownloadRefusalTransition.Appeared), message);

        // And the webhook URL — the credential — is in no part of what was posted.
        Assert.DoesNotContain("placeholder-refusal-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_muted_trigger_delivers_nothing_while_the_other_edge_still_does()
    {
        // Per-trigger muting works for these exactly as for the other four, and the two edges are
        // independently mutable. The POSITIVE CONTROL is the second half: asserting only that the
        // muted edge posted nothing would pass just as well if the notifier were broken outright.
        var notifier = await CreateNotifierAsync(
            triggers: NotificationSettings.AllTriggers
                .Where(t => t != NotificationTrigger.DownloadRefused)
                .ToHashSet());

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Appeared);
        Assert.Empty(_handler.Bodies);

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Cleared);

        var body = Assert.Single(_handler.Bodies);
        Assert.Equal("downloadRefusalCleared", TriggerOf(body));
    }

    [Fact]
    public async Task The_master_switch_being_off_delivers_nothing()
    {
        var notifier = await CreateNotifierAsync(enabled: false);

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Appeared);
        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Cleared);

        Assert.Empty(_handler.Bodies);
    }

    [Fact]
    public async Task A_delivery_records_its_outcome_the_way_every_other_notification_does()
    {
        // The operator's "last delivery" indicator reads one field, so a notice delivered through
        // this path must write it too — otherwise a refusal notice that failed would leave the
        // health surface claiming the last delivery succeeded.
        var notifier = await CreateNotifierAsync();

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Appeared);

        using var scope = _provider!.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        var last = await repository.GetLastDeliveryAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(NotificationDeliveryOutcome.Delivered, last!.Value.Outcome);
        Assert.Equal(_time.GetUtcNow(), last.Value.At);
    }

    [Fact]
    public async Task An_unconfigured_webhook_is_recorded_rather_than_thrown()
    {
        // A refusal notice fires from the download path. An operator who enabled notifications but
        // never set a URL must get a recorded NotConfigured outcome, not an exception travelling
        // back towards a request that has already failed.
        var notifier = await CreateNotifierAsync(webhookUrl: null);

        await notifier.NotifyAsync(SourceName, DownloadRefusalTransition.Appeared);

        Assert.Empty(_handler.Bodies);

        using var scope = _provider!.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<NotificationRepository>()
            .GetLastDeliveryAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(NotificationDeliveryOutcome.NotConfigured, last!.Value.Outcome);
    }

    [Fact]
    public async Task The_tracker_decorator_wired_to_this_notifier_delivers_on_edges_and_is_silent_between_them()
    {
        // The two halves together, which is what Program.cs composes: the decorator decides WHEN,
        // the notifier decides WHAT. Driven through NotifyAsync rather than the background
        // fire-and-forget so the assertions are deterministic rather than racing a Task.Run.
        var notifier = await CreateNotifierAsync();

        var pending = new List<Task>();
        var tracker = new NotifyingDownloadRefusalTracker(
            new DownloadRefusalTracker(),
            (source, transition) => pending.Add(notifier.NotifyAsync(source, transition)));

        var at = _time.GetUtcNow();
        tracker.RecordRefusal(SourceName, "Refused HTTP 302: the source redirected instead of serving the file.", at);
        await Task.WhenAll(pending);

        // Control: the first edge really did post, so the silence asserted next is real.
        Assert.Single(_handler.Bodies);

        tracker.RecordRefusal(SourceName, "Refused HTTP 302: the source redirected instead of serving the file.", at.AddMinutes(1));
        tracker.RecordRefusal(SourceName, "Refused HTTP 302: the source redirected instead of serving the file.", at.AddMinutes(2));
        await Task.WhenAll(pending);

        Assert.Single(_handler.Bodies);

        tracker.RecordSuccessfulGrab(SourceName);
        await Task.WhenAll(pending);

        Assert.Equal(2, _handler.Bodies.Count);
        Assert.Equal("downloadRefused", TriggerOf(_handler.Bodies[0]));
        Assert.Equal("downloadRefusalCleared", TriggerOf(_handler.Bodies[1]));
    }
}
