using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-ln0: the sticky download-refusal health tracker. The point of these tests is the CLEARING
/// rule — a refusal must survive everything except an actual successful grab from the same source,
/// because a redirect-mode upstream keeps refusing until an operator changes the setting.
/// </summary>
public class DownloadRefusalTrackerTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_tracker_reports_nothing()
    {
        var tracker = new DownloadRefusalTracker();

        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void A_recorded_refusal_is_present_in_the_snapshot()
    {
        var tracker = new DownloadRefusalTracker();

        tracker.RecordRefusal("nzbhydra2", "Refused HTTP 302: the source redirected instead of serving the file.", At);

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", refusal.SourceName);
        Assert.Contains("302", refusal.Reason);
        Assert.Equal(At, refusal.ObservedSinceUtc);
        Assert.Equal(At, refusal.LastObservedUtc);
    }

    [Fact]
    public void A_successful_grab_from_the_same_source_clears_the_refusal()
    {
        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("nzbhydra2", "refused", At);

        tracker.RecordSuccessfulGrab("nzbhydra2");

        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void A_successful_grab_from_another_source_does_not_clear_the_refusal()
    {
        // The tracker is per source: a working second source proves nothing about the first, and
        // clearing on it would make the banner vanish while every download from the broken source
        // still fails.
        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("nzbhydra2", "refused", At);

        tracker.RecordSuccessfulGrab("other-source");

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", refusal.SourceName);
    }

    [Fact]
    public void A_successful_grab_for_a_source_that_never_refused_is_a_no_op()
    {
        var tracker = new DownloadRefusalTracker();

        tracker.RecordSuccessfulGrab("never-seen");

        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void Time_passing_does_not_clear_a_refusal()
    {
        // There is deliberately no expiry: the tracker holds no clock and reads none. A refusal
        // recorded a week ago (in the source's own timeline) is still outstanding, because only a
        // successful grab can retire it. Recording a much later timestamp exercises the only way
        // time enters this type at all.
        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("nzbhydra2", "refused", At);

        tracker.RecordRefusal("nzbhydra2", "refused", At.AddDays(7));

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", refusal.SourceName);
    }

    [Fact]
    public void Repeated_refusals_keep_the_first_observed_time_and_advance_the_last()
    {
        // Sonarr retries a failed download, so this path repeats. ObservedSinceUtc must not reset,
        // or an hours-old misconfiguration reads as brand new on every retry.
        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("nzbhydra2", "first", At);

        tracker.RecordRefusal("nzbhydra2", "second", At.AddMinutes(30));

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal(At, refusal.ObservedSinceUtc);
        Assert.Equal(At.AddMinutes(30), refusal.LastObservedUtc);
        Assert.Equal("second", refusal.Reason);
    }

    [Fact]
    public void A_cleared_source_that_refuses_again_starts_a_new_observation_window()
    {
        // The inverse of the test above: once the condition is genuinely over, the next occurrence
        // is a NEW one and must not claim to have been running since before the fix.
        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("nzbhydra2", "first", At);
        tracker.RecordSuccessfulGrab("nzbhydra2");

        tracker.RecordRefusal("nzbhydra2", "again", At.AddHours(2));

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal(At.AddHours(2), refusal.ObservedSinceUtc);
    }

    [Fact]
    public void Refusals_from_several_sources_are_tracked_separately_and_ordered_by_name()
    {
        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("zeta", "refused", At);
        tracker.RecordRefusal("alpha", "refused", At);

        var snapshot = tracker.Snapshot();

        Assert.Equal(new[] { "alpha", "zeta" }, snapshot.Select(r => r.SourceName).ToArray());
    }

    [Fact]
    public async Task The_null_tracker_records_nothing_and_reports_nothing()
    {
        IDownloadRefusalTracker tracker = NullDownloadRefusalTracker.Instance;

        await tracker.RecordRefusalAsync("nzbhydra2", "refused", At);

        Assert.Empty(tracker.Snapshot());
    }
}
