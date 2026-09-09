using Arbitarr.Core.Notifications;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Arbitarr.Data.Notifications;
using Arbitarr.Host.Notifications;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// #57's evaluation cycle: the seam where the shared event store, the policy and the transport
/// meet. <see cref="Arbitarr.Core.Tests.NotificationPolicyTests"/> pins the policy exhaustively as
/// a pure function; this file pins the parts only the composition can get wrong — the store's kinds
/// being translated into the policy's, the cursor advancing so a restart does not re-notify, and
/// the URL never reaching the class that decides what to send.
///
/// <para><b>It POLLS.</b> The dispatcher reads through <c>EventRepository.QueryAsync</c>'s
/// seek-cursor path (#55) rather than subscribing to anything, so these tests drive cycles directly
/// instead of awaiting an observer — which is also why the behaviour is testable without a timer.
/// </para>
///
/// All webhook URLs are obviously-fake <c>example.com</c> forms.
/// </summary>
public sealed class NotificationDispatcherTests : IDisposable
{
    private const string SecretWebhookUrl = "https://example.com/hooks/placeholder-dispatcher-token";

    private readonly SqliteTestDatabase _database = new("arr-searcher-dispatcher-test");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    public NotificationDispatcherTests()
    {
    }

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    /// <summary>
    /// Captures what was posted, so the "no URL in the payload" assertion can inspect the real
    /// serialized body rather than trusting the type's shape.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public List<string> Uris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri?.ToString() ?? string.Empty);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        }
    }

    private async Task<(NotificationDispatcher Dispatcher, CapturingHandler Handler)> CreateDispatcherAsync(
        ArbitarrDbContext context,
        NotificationSettings settings,
        string? webhookUrl = SecretWebhookUrl)
    {
        var notifications = new NotificationRepository(context, _time);
        await notifications.SetSettingsAsync(settings, webhookUrl, CancellationToken.None);

        var handler = new CapturingHandler();
        var transport = new WebhookNotificationTransport(new HttpClient(handler));

        return (new NotificationDispatcher(
            new EventRepository(context, _time),
            notifications,
            transport,
            _time), handler);
    }

    private static NotificationSettings Enabled(int threshold = 3) => new(
        Enabled: true,
        ConsecutiveFailureThreshold: threshold,
        SuppressionRateThreshold: 0.5,
        SuppressionRateWindow: TimeSpan.FromHours(1),
        EnabledTriggers: NotificationSettings.AllTriggers);

    private static Task RecordFailureAsync(EventRepository events, string source) =>
        events.AddAsync(EventKind.SourceFailed, "Source failed", "timeout", source, null, CancellationToken.None);

    [Fact]
    public async Task A_source_crossing_the_threshold_notifies_once_through_the_whole_stack()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < 3; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
        }

        var delivered = await dispatcher.RunCycleAsync();

        var notification = Assert.Single(delivered);
        Assert.Equal(NotificationTrigger.SourceFailing, notification.Trigger);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task A_second_cycle_over_the_same_rows_does_not_re_notify()
    {
        // The cursor is what makes this true, and it is the AC7 property: a restart (or merely the
        // next tick) must not re-send what the operator was already told.
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < 3; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
        }

        Assert.Single(await dispatcher.RunCycleAsync());

        // Non-vacuous: the first cycle really did deliver, so an empty second cycle is the cursor
        // working rather than nothing ever having happened.
        Assert.Single(handler.Bodies);
        Assert.Empty(await dispatcher.RunCycleAsync());
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task A_successful_cycle_after_a_failure_notifies_recovery()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, _) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < 3; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
        }

        Assert.Single(await dispatcher.RunCycleAsync());

        // The store has no "source recovered" kind and needs none: a completed worker cycle for the
        // source IS the recovery signal.
        await events.AddAsync(
            EventKind.WorkerCycle, "Worker cycle completed", null, "placeholder-source", null, CancellationToken.None);

        var recovery = Assert.Single(await dispatcher.RunCycleAsync());
        Assert.Equal(NotificationTrigger.SourceRecovered, recovery.Trigger);
    }

    [Fact]
    public async Task A_disabled_notifier_delivers_nothing_and_does_not_bank_a_backlog()
    {
        // Disabled must not silently accumulate history that all fires the moment it is enabled:
        // the cursor advances to the head without notifying, so switching on starts from now.
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(
            context,
            Enabled() with { Enabled = false });

        for (var i = 0; i < 5; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
        }

        Assert.Empty(await dispatcher.RunCycleAsync());
        Assert.Empty(handler.Bodies);

        var state = await new NotificationRepository(context, _time).GetStateAsync(CancellationToken.None);
        Assert.NotNull(state.Cursor);
    }

    [Fact]
    public async Task The_posted_payload_never_contains_the_webhook_url()
    {
        // The class that decides WHAT to send never learns WHERE: the dispatcher hands the payload
        // to the transport, which reads the URL itself at the moment of the POST. This asserts the
        // real serialized body, non-vacuously — the URL is confirmed configured and confirmed used
        // as the request target first.
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < 3; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
        }

        Assert.Single(await dispatcher.RunCycleAsync());

        // Non-vacuous: the URL really was in play — it is the address that was posted to.
        var uri = Assert.Single(handler.Uris);
        Assert.Equal(SecretWebhookUrl, uri);

        // ...and yet it appears nowhere in what was sent.
        var body = Assert.Single(handler.Bodies);
        Assert.DoesNotContain(SecretWebhookUrl, body, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder-dispatcher-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delivery_attempt_writes_no_event_row_carrying_the_url()
    {
        // /api/activity is un-gated, and providers put the token in the URL path, so a URL on an
        // event row is a credential disclosure to any LAN client. Nothing on the notification path
        // may write one.
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, _) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < 3; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
        }

        Assert.Single(await dispatcher.RunCycleAsync());

        var rows = await context.Set<EventEntry>().AsNoTracking().ToListAsync();
        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            Assert.DoesNotContain(SecretWebhookUrl, row.Summary ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretWebhookUrl, row.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretWebhookUrl, row.Detail ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretWebhookUrl, row.SourceDisplayName ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_delivery_failure_records_the_outcome_and_never_fails_the_cycle()
    {
        // §3.4/AC6: a broken webhook must never fail the operation that produced the event. With no
        // URL configured at all, the cycle must still complete and the outcome must be visible.
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, _) = await CreateDispatcherAsync(context, Enabled(), webhookUrl: null);

        for (var i = 0; i < 3; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
        }

        Assert.Single(await dispatcher.RunCycleAsync());

        var last = await new NotificationRepository(context, _time).GetLastDeliveryAsync(CancellationToken.None);
        Assert.NotNull(last);
        Assert.Equal(NotificationDeliveryOutcome.NotConfigured, last!.Value.Outcome);
    }

    [Fact]
    public async Task A_shadow_flagged_decision_is_counted_in_the_denominator_but_not_the_numerator()
    {
        // The discriminator is the structured `shadow=` token FilterStage writes into Detail, never
        // the English summary — rewording a display string must not change the rate an operator is
        // notified about. Enough shadow-only decisions to clear the minimum sample must therefore
        // produce a 0% rate and no notification.
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < NotificationSettings.MinimumSuppressionSample + 5; i++)
        {
            await events.AddAsync(
                EventKind.Decision,
                "Release flagged in shadow mode (still served)",
                "rule matched",
                null,
                "layer=placeholder; release=placeholder; shadow=True",
                CancellationToken.None);
        }

        Assert.Empty(await dispatcher.RunCycleAsync());
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task An_enforced_suppression_rate_over_the_threshold_notifies_once()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, _) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < NotificationSettings.MinimumSuppressionSample + 5; i++)
        {
            await events.AddAsync(
                EventKind.Decision,
                "Release suppressed",
                "rule matched",
                null,
                "layer=placeholder; release=placeholder; shadow=False",
                CancellationToken.None);
        }

        var notification = Assert.Single(await dispatcher.RunCycleAsync());
        Assert.Equal(NotificationTrigger.SuppressionRateHigh, notification.Trigger);
    }
}
