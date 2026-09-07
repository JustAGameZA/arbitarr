using Arbitarr.Core.Notifications;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// #57 §3.1's policy, which is the whole design constraint of the issue: a source failure notifies
/// on the TRANSITION (once in, once out), while a suppression notifies only on a RATE crossing a
/// threshold over a window — never per suppression. §5's test list is enumerated here.
///
/// These are pure function calls: <see cref="NotificationPolicy"/> performs no I/O and holds no
/// state, so every edge (including "the N+1th does not re-notify", the case a stateless notifier
/// gets wrong) is directly assertable without a database, a clock, or an HTTP endpoint.
/// </summary>
public sealed class NotificationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static NotificationSettings Settings(
        int failureThreshold = 3,
        double rateThreshold = 0.5,
        IReadOnlySet<NotificationTrigger>? triggers = null) =>
        new(
            Enabled: true,
            ConsecutiveFailureThreshold: failureThreshold,
            SuppressionRateThreshold: rateThreshold,
            SuppressionRateWindow: TimeSpan.FromHours(1),
            EnabledTriggers: triggers ?? NotificationSettings.AllTriggers);

    private static NotificationObservation Failure(string source, int offsetSeconds = 0) =>
        new(ObservedEventKind.SourceFailure, source, Now.AddSeconds(offsetSeconds));

    private static NotificationObservation Success(string source, int offsetSeconds = 0) =>
        new(ObservedEventKind.SourceSuccess, source, Now.AddSeconds(offsetSeconds));

    private static List<NotificationObservation> Decisions(int enforced, int shadowed)
    {
        var batch = new List<NotificationObservation>();
        for (var i = 0; i < enforced; i++)
        {
            batch.Add(new NotificationObservation(ObservedEventKind.EnforcedSuppression, null, Now));
        }

        for (var i = 0; i < shadowed; i++)
        {
            batch.Add(new NotificationObservation(ObservedEventKind.ShadowedSuppression, null, Now));
        }

        return batch;
    }

    [Fact]
    public void A_single_failure_does_not_notify()
    {
        var policy = new NotificationPolicy(Settings(failureThreshold: 3));

        var decision = policy.Evaluate(NotificationState.Empty, [Failure("placeholder-source")], cursor: 1, Now);

        Assert.Empty(decision.Notifications);
        Assert.Equal(1, decision.State.FailingSources["placeholder-source"]);
    }

    [Fact]
    public void Reaching_the_consecutive_threshold_notifies_exactly_once()
    {
        var policy = new NotificationPolicy(Settings(failureThreshold: 3));

        var decision = policy.Evaluate(
            NotificationState.Empty,
            [Failure("placeholder-source", 0), Failure("placeholder-source", 1), Failure("placeholder-source", 2)],
            cursor: 3,
            Now);

        var notification = Assert.Single(decision.Notifications);
        Assert.Equal(NotificationTrigger.SourceFailing, notification.Trigger);
        Assert.Equal("placeholder-source", notification.SourceDisplayName);
    }

    [Fact]
    public void Failures_past_the_threshold_do_not_re_notify()
    {
        // §5: "N consecutive failures do; the N+1th does NOT re-notify". This is the assertion a
        // stateless notifier fails, and the reason the policy carries state at all.
        var policy = new NotificationPolicy(Settings(failureThreshold: 3));

        var first = policy.Evaluate(
            NotificationState.Empty,
            [Failure("placeholder-source", 0), Failure("placeholder-source", 1), Failure("placeholder-source", 2)],
            cursor: 3,
            Now);
        Assert.Single(first.Notifications);

        var second = policy.Evaluate(first.State, [Failure("placeholder-source", 3)], cursor: 4, Now);

        Assert.Empty(second.Notifications);
        Assert.Equal(4, second.State.FailingSources["placeholder-source"]);
    }

    [Fact]
    public void Recovery_notifies_exactly_once_and_clears_the_state()
    {
        var policy = new NotificationPolicy(Settings(failureThreshold: 2));

        var failed = policy.Evaluate(
            NotificationState.Empty,
            [Failure("placeholder-source", 0), Failure("placeholder-source", 1)],
            cursor: 2,
            Now);
        Assert.Single(failed.Notifications);

        var recovered = policy.Evaluate(failed.State, [Success("placeholder-source", 2)], cursor: 3, Now);

        var notification = Assert.Single(recovered.Notifications);
        Assert.Equal(NotificationTrigger.SourceRecovered, notification.Trigger);
        Assert.False(recovered.State.FailingSources.ContainsKey("placeholder-source"));

        // A second success produces nothing: the source is no longer tracked as failing.
        var again = policy.Evaluate(recovered.State, [Success("placeholder-source", 3)], cursor: 4, Now);
        Assert.Empty(again.Notifications);
    }

    [Fact]
    public void A_source_that_never_crossed_the_threshold_does_not_notify_recovery()
    {
        // Telling an operator a source "recovered" when they were never told it was failing reports
        // a problem they never had.
        var policy = new NotificationPolicy(Settings(failureThreshold: 3));

        var blipped = policy.Evaluate(
            NotificationState.Empty,
            [Failure("placeholder-source", 0), Failure("placeholder-source", 1)],
            cursor: 2,
            Now);
        Assert.Empty(blipped.Notifications);

        var recovered = policy.Evaluate(blipped.State, [Success("placeholder-source", 2)], cursor: 3, Now);

        Assert.Empty(recovered.Notifications);
        Assert.False(recovered.State.FailingSources.ContainsKey("placeholder-source"));
    }

    [Fact]
    public void Failure_counts_are_tracked_per_source()
    {
        var policy = new NotificationPolicy(Settings(failureThreshold: 2));

        var decision = policy.Evaluate(
            NotificationState.Empty,
            [Failure("placeholder-alpha", 0), Failure("placeholder-beta", 1), Failure("placeholder-alpha", 2)],
            cursor: 3,
            Now);

        // Alpha reached two and notified; beta is at one and did not.
        var notification = Assert.Single(decision.Notifications);
        Assert.Equal("placeholder-alpha", notification.SourceDisplayName);
        Assert.Equal(1, decision.State.FailingSources["placeholder-beta"]);
    }

    [Fact]
    public void Suppression_rate_below_the_threshold_does_not_notify()
    {
        var policy = new NotificationPolicy(Settings(rateThreshold: 0.5));

        // 5 of 25 enforced = 20%, well under the threshold, and over the minimum sample.
        var decision = policy.Evaluate(NotificationState.Empty, Decisions(enforced: 5, shadowed: 20), cursor: 25, Now);

        Assert.Empty(decision.Notifications);
        Assert.False(decision.State.SuppressionRateHigh);
    }

    [Fact]
    public void Crossing_the_suppression_rate_threshold_notifies_once_and_does_not_repeat()
    {
        // §5: "crossing it does; staying above it does not re-notify every evaluation".
        var policy = new NotificationPolicy(Settings(rateThreshold: 0.5));

        var crossed = policy.Evaluate(NotificationState.Empty, Decisions(enforced: 20, shadowed: 5), cursor: 25, Now);

        var notification = Assert.Single(crossed.Notifications);
        Assert.Equal(NotificationTrigger.SuppressionRateHigh, notification.Trigger);
        Assert.True(crossed.State.SuppressionRateHigh);

        var stillHigh = policy.Evaluate(crossed.State, Decisions(enforced: 22, shadowed: 3), cursor: 50, Now);

        Assert.Empty(stillHigh.Notifications);
        Assert.True(stillHigh.State.SuppressionRateHigh);
    }

    [Fact]
    public void The_suppression_rate_falling_back_notifies_the_closing_edge()
    {
        var policy = new NotificationPolicy(Settings(rateThreshold: 0.5));

        var crossed = policy.Evaluate(NotificationState.Empty, Decisions(enforced: 20, shadowed: 5), cursor: 25, Now);
        Assert.True(crossed.State.SuppressionRateHigh);

        var recovered = policy.Evaluate(crossed.State, Decisions(enforced: 2, shadowed: 23), cursor: 50, Now);

        var notification = Assert.Single(recovered.Notifications);
        Assert.Equal(NotificationTrigger.SuppressionRateNormal, notification.Trigger);
        Assert.False(recovered.State.SuppressionRateHigh);
    }

    [Fact]
    public void A_sample_below_the_minimum_does_not_produce_a_rate()
    {
        // One suppression out of one decision is a 100% rate. Without the sample floor a quiet
        // homelab would page its operator every time a single release was filtered overnight —
        // the exact noise that makes people mute the feature.
        var policy = new NotificationPolicy(Settings(rateThreshold: 0.5));

        var decision = policy.Evaluate(NotificationState.Empty, Decisions(enforced: 1, shadowed: 0), cursor: 1, Now);

        Assert.Empty(decision.Notifications);
        Assert.False(decision.State.SuppressionRateHigh);
    }

    [Fact]
    public void Decisions_older_than_the_window_are_not_counted()
    {
        var policy = new NotificationPolicy(Settings(rateThreshold: 0.5));

        var stale = Enumerable.Range(0, 30)
            .Select(_ => new NotificationObservation(
                ObservedEventKind.EnforcedSuppression, null, Now - TimeSpan.FromHours(2)))
            .ToList();

        var decision = policy.Evaluate(NotificationState.Empty, stale, cursor: 30, Now);

        // Every row is outside the one-hour window, so the sample is empty and no rate exists.
        Assert.Empty(decision.Notifications);
        Assert.False(decision.State.SuppressionRateHigh);
    }

    [Fact]
    public void A_disabled_trigger_is_not_delivered_but_still_advances_the_state()
    {
        // Muting a trigger must not desynchronise the state machine, or re-enabling it would
        // resurrect a stale transition (or lose the matching recovery).
        var policy = new NotificationPolicy(Settings(
            failureThreshold: 2,
            triggers: new HashSet<NotificationTrigger> { NotificationTrigger.SourceRecovered }));

        var failed = policy.Evaluate(
            NotificationState.Empty,
            [Failure("placeholder-source", 0), Failure("placeholder-source", 1)],
            cursor: 2,
            Now);

        Assert.Empty(failed.Notifications);
        Assert.Equal(2, failed.State.FailingSources["placeholder-source"]);

        // Recovery IS enabled, and still fires, because the state was tracked regardless.
        var recovered = policy.Evaluate(failed.State, [Success("placeholder-source", 2)], cursor: 3, Now);
        Assert.Equal(NotificationTrigger.SourceRecovered, Assert.Single(recovered.Notifications).Trigger);
    }

    [Fact]
    public void A_disabled_notifier_delivers_nothing_at_all()
    {
        var settings = NotificationSettings.Default with { Enabled = false, ConsecutiveFailureThreshold = 2 };
        var policy = new NotificationPolicy(settings);

        var decision = policy.Evaluate(
            NotificationState.Empty,
            [Failure("placeholder-source", 0), Failure("placeholder-source", 1)],
            cursor: 2,
            Now);

        Assert.Empty(decision.Notifications);
    }

    [Fact]
    public void The_cursor_is_carried_into_the_new_state()
    {
        // AC7's restart behaviour rests on this: the cursor is what a restarted notifier resumes
        // from, and it orders by event Id rather than time for the reason EventQuery.Cursor states.
        var policy = new NotificationPolicy(Settings());

        var decision = policy.Evaluate(NotificationState.Empty, [Failure("placeholder-source")], cursor: 4242, Now);

        Assert.Equal(4242, decision.State.Cursor);
    }

    [Fact]
    public void A_null_cursor_leaves_the_prior_position_untouched()
    {
        var policy = new NotificationPolicy(Settings());
        var prior = NotificationState.Empty with { Cursor = 99 };

        var decision = policy.Evaluate(prior, [], cursor: null, Now);

        Assert.Equal(99, decision.State.Cursor);
    }

    [Fact]
    public void A_failure_with_no_named_source_is_ignored()
    {
        var policy = new NotificationPolicy(Settings(failureThreshold: 2));

        var decision = policy.Evaluate(
            NotificationState.Empty,
            [
                new NotificationObservation(ObservedEventKind.SourceFailure, null, Now),
                new NotificationObservation(ObservedEventKind.SourceFailure, "  ", Now),
            ],
            cursor: 2,
            Now);

        Assert.Empty(decision.Notifications);
        Assert.Empty(decision.State.FailingSources);
    }

    [Fact]
    public void State_carried_across_a_restart_does_not_re_notify_a_known_failing_source()
    {
        // AC7 in one assertion: a source already reported as failing before a restart is loaded
        // back from persisted state and produces no duplicate, only its eventual recovery.
        var policy = new NotificationPolicy(Settings(failureThreshold: 3));

        var afterRestart = new NotificationState(
            Cursor: 500,
            FailingSources: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["placeholder-source"] = 7 },
            SuppressionRateHigh: false);

        var decision = policy.Evaluate(afterRestart, [Failure("placeholder-source", 1)], cursor: 501, Now);
        Assert.Empty(decision.Notifications);

        var recovered = policy.Evaluate(decision.State, [Success("placeholder-source", 2)], cursor: 502, Now);
        Assert.Equal(NotificationTrigger.SourceRecovered, Assert.Single(recovered.Notifications).Trigger);
    }
}
