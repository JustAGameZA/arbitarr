namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// The rule for folding repeated identical events onto one stored row (arb-itw / audit F-008).
///
/// WHY WRITE-SIDE RATHER THAN READ-SIDE. The flood is the stored rows themselves, not how any one
/// surface renders them. A refused download retried by Sonarr wrote 71 identical
/// <c>SourceFailed</c> rows in six minutes; grouping those at read time would leave all 71 in the
/// database, still consuming the retention budget and still pushing every other event out of the
/// window that retention keeps. It would also have to be implemented again in each reader — the
/// Activity page and the notifier fold both read this store — and two implementations of "the same
/// event" drift. Folding once, at the point of writing, gives every reader the same answer for
/// free.
///
/// WHY NOT DE-DUPLICATE. A coalesced row keeps a count rather than discarding the repeats: "this
/// failed 71 times" and "this failed" are different operational facts, and the second is the one
/// that gets a problem ignored.
///
/// WHAT NEVER FOLDS. Decisions are excluded by kind, and that exclusion is a correctness rule
/// rather than a tuning choice. The rule itself lives on <c>EventRepository.MayCoalesce</c>,
/// because it is expressed against Data's own <c>EventKind</c> and Core may not reference Data;
/// only the window is policy general enough to state here.
/// </summary>
public static class EventCoalescing
{
    /// <summary>
    /// How long after an event a subsequent identical one folds onto it instead of inserting a row.
    ///
    /// Ten minutes is chosen against the flood this addresses — a retry storm lasting six — so a
    /// burst folds into one row while a fault that recurs hours later still reads as a separate
    /// occurrence rather than silently inflating a count on a stale row. The window is measured
    /// from the row's LAST activity, not its first, so a persistent storm keeps folding onto one
    /// row instead of starting a fresh one every ten minutes.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
}
