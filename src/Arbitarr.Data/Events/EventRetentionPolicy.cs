using Arbitarr.Data.Entities;

namespace Arbitarr.Data.Events;

/// <summary>
/// The single named place holding how long each <see cref="EventKind"/> is retained (plan §2: "state
/// the chosen figures when this executes" — chosen here rather than left to whoever wires up
/// pruning next). Do not scatter these figures as literals elsewhere; look them up here.
///
/// The two windows are deliberately different, and that asymmetry is the non-obvious part a future
/// reader might "simplify" back to one value — don't:
///
/// - <see cref="DecisionRetention"/> (180 days) is long because #54's agreement rate ("the pipeline
///   has been right 47 of 52 times this week") is only meaningful if enough history survives to
///   compute it over. Pruning decisions on the same short clock as routine events would silently
///   erode that statistic's sample size.
/// - <see cref="OperationalRetention"/> (7 days) is short because worker-cycle/snapshot/search/source
///   events are comparatively high-volume and low-value after about a week — nobody is asking "what
///   did the worker do a month ago" the way they ask "has shadow mode earned my trust". Keeping them
///   at 180 days too would let the routine, high-frequency kinds dominate table growth on the SQLite
///   file in the config bind mount for no benefit anyone has asked for.
/// </summary>
public static class EventRetentionPolicy
{
    /// <summary>Retention window for <see cref="EventKind.Decision"/> rows.</summary>
    public static readonly TimeSpan DecisionRetention = TimeSpan.FromDays(180);

    /// <summary>Retention window for every non-decision (operational) event kind.</summary>
    public static readonly TimeSpan OperationalRetention = TimeSpan.FromDays(7);

    /// <summary>The retention window that applies to <paramref name="kind"/>.</summary>
    public static TimeSpan For(EventKind kind) => kind switch
    {
        EventKind.Decision => DecisionRetention,
        _ => OperationalRetention,
    };
}
