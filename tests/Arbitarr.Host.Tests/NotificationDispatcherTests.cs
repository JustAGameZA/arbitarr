using Arbitarr.Core.Diagnostics;
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
/// meet. <c>Arbitarr.Core.Tests.NotificationPolicyTests</c> pins the policy exhaustively as
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

    /// <summary>
    /// arb-itw: the threshold counts FAILURES, not stored rows, and this pins the seam where that
    /// distinction is made. Since coalescing, three identical failures inside the window are ONE
    /// row carrying RepeatCount 3 — so a dispatcher that counted rows would see a single failure,
    /// never reach a threshold of three, and silently stop reporting a source as down. That is the
    /// regression this guards, and the policy's own unit tests cannot see it because the folding
    /// happens in the store beneath them.
    ///
    /// The row count is asserted FIRST and deliberately: without it this test would still pass if
    /// coalescing stopped happening altogether, for the entirely different reason that there were
    /// three separate rows to count. Pinning "one row, and it still notified" is what makes it bite.
    /// </summary>
    [Fact]
    public async Task Failures_folded_onto_one_row_still_reach_the_consecutive_threshold()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled());

        for (var i = 0; i < 3; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        var row = Assert.Single(await events.GetAllAsync(CancellationToken.None));
        Assert.Equal(3, row.RepeatCount);

        var notification = Assert.Single(await dispatcher.RunCycleAsync());
        Assert.Equal(NotificationTrigger.SourceFailing, notification.Trigger);
        Assert.Single(handler.Bodies);
    }

    /// <summary>
    /// arb-u8e, the bug itself: failures that arrive AFTER the notifier has already folded the row
    /// in must still reach the threshold. Since coalescing a repeat folds onto the EXISTING row —
    /// same Id, RepeatCount incremented — so an Id-only cursor never saw it again and the source
    /// could fail forever without being reported down.
    ///
    /// The first cycle is deliberately below the threshold and asserted empty: that is what puts the
    /// row under the cursor, which is the only state in which the bug exists. Against master this
    /// test fails at the final assertion with zero notifications.
    ///
    /// The row count is asserted so the test cannot pass for the wrong reason — if coalescing
    /// stopped, the later failures would be NEW rows above the cursor and the old read path would
    /// find them without any of this.
    /// </summary>
    [Fact]
    public async Task Failures_repeating_after_the_cursor_passed_the_row_still_reach_the_threshold()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled(threshold: 3));

        // One failure, folded in below the threshold: the row is now under the cursor.
        await RecordFailureAsync(events, "placeholder-source");
        Assert.Empty(await dispatcher.RunCycleAsync());

        // Two more failures, each its own cycle, each folding onto that same row.
        for (var i = 0; i < 2; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await RecordFailureAsync(events, "placeholder-source");

            var row = Assert.Single(await events.GetAllAsync(CancellationToken.None));
            Assert.Equal(i + 2, row.RepeatCount);

            if (i == 0)
            {
                // Still one short of the threshold, so still nothing — and this proves the
                // notification below came from the LAST repeat rather than from the first re-read.
                Assert.Empty(await dispatcher.RunCycleAsync());
            }
        }

        var notification = Assert.Single(await dispatcher.RunCycleAsync());
        Assert.Equal(NotificationTrigger.SourceFailing, notification.Trigger);
        Assert.Single(handler.Bodies);
    }

    /// <summary>
    /// The control for the test above: the same shape, the same number of cycles, but no repeats.
    /// One failure and then nothing must never reach a threshold of three, or the re-read would be
    /// manufacturing failures rather than surfacing ones the store already recorded.
    ///
    /// This is what makes the re-read falsifiable. Without it, an implementation that re-presented
    /// every row on every pass would pass the test above and look correct.
    /// </summary>
    [Fact]
    public async Task A_row_that_is_not_repeating_is_never_re_presented()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled(threshold: 3));

        await RecordFailureAsync(events, "placeholder-source");
        Assert.Empty(await dispatcher.RunCycleAsync());

        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            Assert.Empty(await dispatcher.RunCycleAsync());
        }

        Assert.Empty(handler.Bodies);

        // Non-vacuous: the row really is there and really is under the cursor — this is the same
        // state the test above notified from, differing only in that nothing repeated.
        Assert.Single(await events.GetAllAsync(CancellationToken.None));
        var state = await new NotificationRepository(context, _time).GetStateAsync(CancellationToken.None);
        Assert.NotNull(state.Cursor);
    }

    /// <summary>
    /// The watermark's own property: a repeat is presented ONCE. A cycle that follows a repeat with
    /// no further repeats must fold nothing, or the count would climb on every tick and any single
    /// stale row would eventually trip the threshold on its own.
    ///
    /// This is what fails against an implementation re-presenting the row's whole RepeatCount each
    /// pass: three failures counted as growth are 1 + 1 + 1 = 3, but counted as successive totals
    /// they are 1 + 2 + 3 = 6, so a threshold of four is crossed that should not be.
    /// </summary>
    [Fact]
    public async Task A_repeat_already_folded_in_is_not_counted_again_on_the_next_cycle()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled(threshold: 4));

        // Three failures in all, one per cycle, all folding onto one row. Counted as growth that is
        // 1 + 1 + 1 = 3, one short of the threshold. Counted as the row's TOTAL RepeatCount each
        // pass it is 1 + 2 + 3 = 6, well past it — so the silence below is the discriminator.
        await RecordFailureAsync(events, "placeholder-source");
        Assert.Empty(await dispatcher.RunCycleAsync());

        for (var i = 0; i < 2; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await RecordFailureAsync(events, "placeholder-source");
            Assert.Empty(await dispatcher.RunCycleAsync());
        }

        // Non-vacuous: it really is one coalesced row, and its total really has climbed to 3 —
        // so a RepeatCount-as-growth implementation had the material to trip the threshold.
        var row = Assert.Single(await events.GetAllAsync(CancellationToken.None));
        Assert.Equal(3, row.RepeatCount);

        // And idle cycles after the repeats add nothing at all: the watermark, not the count.
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            Assert.Empty(await dispatcher.RunCycleAsync());
        }

        Assert.Empty(handler.Bodies);

        // The positive control for the whole test: ONE more failure now reaches 4 and notifies.
        // Without this, the test would pass just as happily against a notifier that had stopped
        // counting repeats altogether — which is the arb-u8e bug it is meant to sit next to.
        _time.Advance(TimeSpan.FromSeconds(30));
        await RecordFailureAsync(events, "placeholder-source");
        Assert.Equal(NotificationTrigger.SourceFailing, Assert.Single(await dispatcher.RunCycleAsync()).Trigger);
    }

    /// <summary>
    /// Why ordering re-read rows by last activity is SAFE, pinned as the property it rests on: a
    /// repeat can never be later than a newer row for the same source, because the coalescer folds
    /// only onto the MOST RECENT row.
    ///
    /// This is the reordering hazard the design review raised — a repeat on a low-Id row carrying an
    /// instant later than a higher-Id recovery, which would make Id order and time order disagree
    /// about whether the source is up. <c>EventRepository.TryCoalesceAsync</c> forecloses it, and
    /// says so: <i>"ONLY THE MOST RECENT ROW IS CONSIDERED... matching an older row across
    /// intervening different events would reorder history"</i>. Once the recovery is written, a
    /// later failure starts a NEW row instead of folding back.
    ///
    /// So this test does not exercise the dispatcher's tie-breaking; it pins the upstream invariant
    /// that lets the dispatcher sort by last activity at all. If coalescing is ever widened to match
    /// any row in the window, this fails, and the ordering in <c>ReadAscendingBatchAsync</c> has to
    /// be revisited at the same time — which is exactly the coupling worth catching in CI.
    /// </summary>
    [Fact]
    public async Task A_failure_after_a_recovery_starts_a_new_row_rather_than_folding_backwards()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var (dispatcher, handler) = await CreateDispatcherAsync(context, Enabled(threshold: 2));

        // Row 1: the source fails. Below the threshold, so it is folded in silently and the row is
        // now under the cursor — the state in which a re-read could happen at all.
        await RecordFailureAsync(events, "placeholder-source");
        Assert.Empty(await dispatcher.RunCycleAsync());
        Assert.Empty(handler.Bodies);

        // Row 2: the source answers again, at a higher Id.
        _time.Advance(TimeSpan.FromSeconds(30));
        await events.AddAsync(
            EventKind.WorkerCycle, "Worker cycle completed", null, "placeholder-source", null, CancellationToken.None);

        // It fails again, WELL inside the coalescing window — so the window is not what stops this.
        // Twice, so the new row carries enough occurrences to cross the threshold on its own.
        _time.Advance(TimeSpan.FromSeconds(30));
        await RecordFailureAsync(events, "placeholder-source");
        _time.Advance(TimeSpan.FromSeconds(30));
        await RecordFailureAsync(events, "placeholder-source");

        // The invariant: three rows, not two. The second failure did NOT fold back onto row 1, so
        // row 1's last activity cannot postdate row 2 and the two orderings cannot disagree.
        var rows = (await events.GetAllAsync(CancellationToken.None)).OrderBy(row => row.Id).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(EventKind.SourceFailed, rows[0].Kind);
        Assert.Equal(EventKind.WorkerCycle, rows[1].Kind);
        Assert.Equal(EventKind.SourceFailed, rows[2].Kind);

        // Non-vacuous: coalescing really is live in this fixture — row 1 folded nothing only because
        // of the intervening recovery, and the window itself is minutes wide.
        Assert.Equal(1, rows[0].RepeatCount);
        Assert.True(rows[2].OccurredAt - rows[0].OccurredAt < EventCoalescing.Window);

        // And the fold still ends where real time does: recovery, then the new failure. The source
        // is down, and the count reaching 2 reports it down.
        Assert.Equal(NotificationTrigger.SourceFailing, Assert.Single(await dispatcher.RunCycleAsync()).Trigger);
        Assert.Single(handler.Bodies);
    }

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

    /// <summary>
    /// The backlog rule applies to BOTH halves of the position. Since arb-u8e the notifier's place
    /// is an Id cursor plus a repeat watermark, and skipping the disabled history by advancing only
    /// the Id would leave every already-repeated row below it looking unseen — so enabling would
    /// replay the coalesced history through the repeat path instead of the cursor path, which is
    /// the same backlog arriving by a different door.
    ///
    /// Positive control: the failures really did fold and really did repeat, so there IS a backlog
    /// for an unadvanced watermark to replay. Threshold 1 makes any replay notify immediately.
    /// </summary>
    [Fact]
    public async Task Enabling_after_a_disabled_period_does_not_replay_repeats_either()
    {
        using var context = CreateContext();
        var events = new EventRepository(context, _time);
        var notifications = new NotificationRepository(context, _time);

        var handler = new CapturingHandler();
        var dispatcher = new NotificationDispatcher(
            events, notifications, new WebhookNotificationTransport(new HttpClient(handler)), _time);

        await notifications.SetSettingsAsync(Enabled(threshold: 2) with { Enabled = false }, SecretWebhookUrl, CancellationToken.None);

        for (var i = 0; i < 4; i++)
        {
            await RecordFailureAsync(events, "placeholder-source");
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        // Non-vacuous: one coalesced row carrying real repeats, which is exactly what would replay.
        var row = Assert.Single(await events.GetAllAsync(CancellationToken.None));
        Assert.Equal(4, row.RepeatCount);
        Assert.NotNull(row.LastRepeatedAt);

        Assert.Empty(await dispatcher.RunCycleAsync());

        var skipped = await notifications.GetStateAsync(CancellationToken.None);
        Assert.NotNull(skipped.Cursor);
        Assert.NotNull(skipped.RepeatsSeenAt);

        // Switched on, with nothing new happening since: the history must stay skipped.
        await notifications.SetSettingsAsync(Enabled(threshold: 2), SecretWebhookUrl, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Empty(await dispatcher.RunCycleAsync());
        Assert.Empty(handler.Bodies);
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
