using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arbitarr.Core.Diagnostics;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-apj: the transition observer around the refusal tracker. The property under test is that the
/// operator hears about the EDGES and only the edges — one notice when a source's health item
/// appears, one when it clears, and silence for every repeat in between.
///
/// <para><b>Every "sends nothing" assertion here sits beside the "did send" assertion for the same
/// sink, in the same test.</b> CLAUDE.md §4: an absence assertion passes just as happily when the
/// mechanism never fired at all, so <c>Assert.Empty(sink)</c> after a repeat proves nothing unless
/// the same sink has already been shown to record. The repeat tests therefore record the first
/// refusal, assert ONE entry, and only then assert that the repeat added none — so a decorator that
/// silently stopped calling the callback fails on the first assertion rather than passing on the
/// second.</para>
/// </summary>
public class NotifyingDownloadRefusalTrackerTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private const string Reason = "Refused HTTP 302: the source redirected instead of serving the file.";

    /// <summary>Records every transition the decorator raises, in order.</summary>
    private sealed class RecordingSink
    {
        public List<(string Source, DownloadRefusalTransition Transition)> Transitions { get; } = new();

        public void Record(string source, DownloadRefusalTransition transition) =>
            Transitions.Add((source, transition));
    }

    private static (NotifyingDownloadRefusalTracker Tracker, RecordingSink Sink) Build()
    {
        var sink = new RecordingSink();
        return (new NotifyingDownloadRefusalTracker(new DownloadRefusalTracker(), sink.Record), sink);
    }

    [Fact]
    public async Task The_first_refusal_for_a_source_raises_one_appeared_transition()
    {
        var (tracker, sink) = Build();

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);

        var transition = Assert.Single(sink.Transitions);
        Assert.Equal("nzbhydra2", transition.Source);
        Assert.Equal(DownloadRefusalTransition.Appeared, transition.Transition);
    }

    [Fact]
    public async Task A_repeated_refusal_while_the_item_is_present_raises_nothing_further()
    {
        // THE POSITIVE CONTROL IS THE FIRST ASSERTION, and it is why the second one bites. A
        // redirect-mode upstream refuses on every one of Sonarr's retries, so "nothing further"
        // is the requirement — but asserting a count of one after the repeats would pass equally
        // well if the decorator had raised nothing at all. Pinning the count BEFORE the repeats
        // proves the sink is live, so the unchanged count afterwards is real silence.
        var (tracker, sink) = Build();

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);
        Assert.Single(sink.Transitions);

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At.AddMinutes(1));
        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At.AddMinutes(2));
        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At.AddMinutes(3));

        var transition = Assert.Single(sink.Transitions);
        Assert.Equal(DownloadRefusalTransition.Appeared, transition.Transition);
    }

    [Fact]
    public async Task A_successful_grab_after_a_refusal_raises_one_cleared_transition()
    {
        var (tracker, sink) = Build();
        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);
        Assert.Single(sink.Transitions);

        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");

        Assert.Equal(2, sink.Transitions.Count);
        Assert.Equal(DownloadRefusalTransition.Appeared, sink.Transitions[0].Transition);
        Assert.Equal("nzbhydra2", sink.Transitions[1].Source);
        Assert.Equal(DownloadRefusalTransition.Cleared, sink.Transitions[1].Transition);
    }

    [Fact]
    public async Task A_successful_grab_with_no_outstanding_refusal_raises_nothing()
    {
        // Every ordinary download calls RecordSuccessfulGrab. Raising a "cleared" notice for a
        // source that was never refused would mean one notification per successful grab, which is
        // the noise that gets a feature muted.
        var (tracker, sink) = Build();

        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");

        Assert.Empty(sink.Transitions);

        // The control: the same sink, same tracker, DOES record when a real edge is crossed — so
        // the emptiness above is the decorator staying silent, not the sink being disconnected.
        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);
        Assert.Single(sink.Transitions);
    }

    [Fact]
    public async Task Repeated_successful_grabs_after_a_clear_raise_nothing_further()
    {
        var (tracker, sink) = Build();
        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);
        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");
        Assert.Equal(2, sink.Transitions.Count);

        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");
        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");

        Assert.Equal(2, sink.Transitions.Count);
    }

    [Fact]
    public async Task A_refusal_after_a_clear_raises_a_second_appeared_transition()
    {
        // The condition genuinely recurring is a new edge, not a repeat: the operator fixed the
        // setting, it was reverted, and they need to hear about it again.
        var (tracker, sink) = Build();
        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);
        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At.AddHours(1));

        Assert.Equal(3, sink.Transitions.Count);
        Assert.Equal(DownloadRefusalTransition.Appeared, sink.Transitions[0].Transition);
        Assert.Equal(DownloadRefusalTransition.Cleared, sink.Transitions[1].Transition);
        Assert.Equal(DownloadRefusalTransition.Appeared, sink.Transitions[2].Transition);
    }

    [Fact]
    public async Task Two_sources_transition_independently()
    {
        // Per source, like the tracker itself. One source refusing must not suppress the other's
        // notice, and one source recovering must not clear the other's.
        var (tracker, sink) = Build();

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);
        await tracker.RecordRefusalAsync("other-source", Reason, At);
        Assert.Equal(2, sink.Transitions.Count);

        // A repeat for one while the other is also present: still nothing.
        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At.AddMinutes(1));
        Assert.Equal(2, sink.Transitions.Count);

        await tracker.RecordSuccessfulGrabAsync("other-source");

        Assert.Equal(3, sink.Transitions.Count);
        Assert.Equal("other-source", sink.Transitions[2].Source);
        Assert.Equal(DownloadRefusalTransition.Cleared, sink.Transitions[2].Transition);

        // The untouched source is still refused, so its item — and its silence — are unaffected.
        var remaining = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", remaining.SourceName);
    }

    [Fact]
    public async Task The_snapshot_passes_through_to_the_inner_tracker_unchanged()
    {
        // The decorator adds an observation, never a second copy of the state. /api/status reads
        // through it, so a divergence here would show the operator a different set of health items
        // than the tracker actually holds.
        var inner = new DownloadRefusalTracker();
        var tracker = new NotifyingDownloadRefusalTracker(inner, (_, _) => { });

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);

        var throughDecorator = Assert.Single(tracker.Snapshot());
        var throughInner = Assert.Single(inner.Snapshot());
        Assert.Equal(throughInner, throughDecorator);
        Assert.Equal(Reason, throughDecorator.Reason);
    }

    [Fact]
    public async Task A_transition_raised_against_an_inner_tracker_that_already_holds_the_source_is_not_re_raised()
    {
        // The edges are read from the INNER tracker's state, not from a private copy — so a tracker
        // that already holds a refusal (arb-v3w rehydrating a persisted one, say) is correctly seen
        // as already-present and produces no "appeared" notice for a condition the operator was
        // already told about. A decorator keeping its own set would have an empty one here and
        // would announce the refusal a second time.
        var inner = new DownloadRefusalTracker();
        await inner.RecordRefusalAsync("nzbhydra2", Reason, At);

        var sink = new RecordingSink();
        var tracker = new NotifyingDownloadRefusalTracker(inner, sink.Record);

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At.AddMinutes(1));

        Assert.Empty(sink.Transitions);

        // Control: the same freshly-wrapped tracker does raise for a source it has NOT already got.
        await tracker.RecordRefusalAsync("other-source", Reason, At.AddMinutes(2));
        var raised = Assert.Single(sink.Transitions);
        Assert.Equal("other-source", raised.Source);
    }

    [Fact]
    public async Task A_callback_that_throws_does_not_fail_the_recording()
    {
        // The callback runs inline on the download path. A broken notifier must cost a notification
        // and nothing else — if this propagated, a misconfigured webhook would turn the refused
        // download's clean 502 into a 500.
        var tracker = new NotifyingDownloadRefusalTracker(
            new DownloadRefusalTracker(),
            (_, _) => throw new InvalidOperationException("the notifier is broken"));

        await tracker.RecordRefusalAsync("nzbhydra2", Reason, At);

        // The state was still recorded, so the health item appears even though the notice did not.
        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", refusal.SourceName);

        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public async Task Parallel_refusals_and_grabs_for_the_same_source_raise_exactly_one_edge_each_way()
    {
        // arb-apj fix-up: the decorator's read-mutate-read triple is now serialised by a private
        // gate, so two concurrent RecordRefusal calls for the same source cannot both observe
        // wasPresent == false. Without that gate this would intermittently count 2 (or more)
        // Appeared/Cleared raises instead of exactly 1 — that is the failure this test would show
        // if the lock were removed; it is not committed here (CLAUDE.md's mutation-testing rule
        // keeps a broken variant out of the repo), but the exact-count assertions below are what
        // would catch it.
        var appeared = 0;
        var cleared = 0;
        var tracker = new NotifyingDownloadRefusalTracker(
            new DownloadRefusalTracker(),
            (_, transition) =>
            {
                if (transition == DownloadRefusalTransition.Appeared)
                {
                    Interlocked.Increment(ref appeared);
                }
                else
                {
                    Interlocked.Increment(ref cleared);
                }
            });

        var refusalTasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await tracker.RecordRefusalAsync("nzbhydra2", Reason, At)));
        await Task.WhenAll(refusalTasks);

        Assert.Equal(1, appeared);
        Assert.Equal(0, cleared);

        var grabTasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await tracker.RecordSuccessfulGrabAsync("nzbhydra2")));
        await Task.WhenAll(grabTasks);

        Assert.Equal(1, appeared);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void The_decorator_rejects_a_null_inner_tracker_or_callback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new NotifyingDownloadRefusalTracker(null!, (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() =>
            new NotifyingDownloadRefusalTracker(new DownloadRefusalTracker(), null!));
    }
}
