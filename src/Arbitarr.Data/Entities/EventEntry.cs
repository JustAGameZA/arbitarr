namespace Arbitarr.Data.Entities;

/// <summary>
/// The shared event store (#55 step 1 / #54's decision store — plan §2, "one store, not two").
/// A row is either a pipeline decision (<see cref="EventKind.Decision"/>, #54's territory — it adds
/// a review verdict on top of these same rows) or one of the non-decision, operational events #55
/// needs (worker cycle, snapshot refresh, search served, source failure).
///
/// No credential ever lands on this entity by design (plan §9): a source is identified by
/// <see cref="SourceDisplayName"/> (a display name/id), never a key. There is deliberately no field
/// shaped like a secret to put one in — the same posture <c>Entities.Source</c> takes with API keys
/// (see that type's doc comment) — so recording a credential here requires actively misusing
/// <see cref="Detail"/>'s free-text payload rather than falling into an obvious column.
/// </summary>
public sealed class EventEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>Which kind of event this row is. See <see cref="EventKind"/> for the full accounting.</summary>
    public EventKind Kind { get; set; }

    /// <summary>When the event occurred (write time, not read time).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// One-line statement of what happened (plan AC2: "and why" lives in <see cref="Reason"/>, not
    /// folded into this). E.g. "Search served from cache", "Worker cycle refreshed 3 snapshots".
    /// </summary>
    public required string Summary { get; set; }

    /// <summary>
    /// Why it happened, when there is a why to state (plan AC2 — a reason, not only an event name).
    /// Null for events where the summary is already the complete answer.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Identifies the source involved (its display name or id), when the event concerns one — e.g.
    /// <see cref="EventKind.SourceFailed"/>. Never a credential; see this type's doc comment.
    /// </summary>
    public string? SourceDisplayName { get; set; }

    /// <summary>
    /// Free-form, kind-specific detail (e.g. the release identity and shadow-mode flag for a
    /// <see cref="EventKind.Decision"/> row). Opaque to this store; each consumer parses what it
    /// recognizes. Never write a credential into this field (plan §9).
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Whether the pipeline was in shadow mode when this decision was made — #54 AC1's "the
    /// shadow-mode state AT DECISION TIME", and what #54's review-queue filter selects on
    /// (plan §4 step 3). Null on every non-<see cref="EventKind.Decision"/> row, where the question
    /// does not apply.
    ///
    /// THIS IS A COLUMN RATHER THAN A SUBSTRING OF <see cref="Detail"/>, AND THAT IS DELIBERATE.
    /// #55's emission already states shadow mode in <see cref="Summary"/> as English prose
    /// ("Release flagged in shadow mode (still served)" vs "Release suppressed"), which is right for
    /// a human reading the Activity surface and wrong for a filter: matching a WHERE clause against
    /// display text couples the query to wording that exists to be edited, so rephrasing a sentence
    /// would silently change which rows the review queue returns. Storing the flag the pipeline
    /// actually held makes the filter answer the question it claims to.
    ///
    /// It is captured at write time and never recomputed, which is the whole point: a decision made
    /// under shadow mode and read after the switch was flipped must still read as a shadow-mode
    /// decision (plan §7's "shadow-mode flag read at display time instead of decision time" risk).
    /// </summary>
    public bool? ShadowMode { get; set; }

    /// <summary>
    /// The operator's verdict on this decision, or null when nobody has reviewed it yet (#54 AC2).
    ///
    /// A NULLABLE COLUMN ON THIS ROW, NEVER A SECOND TABLE — see <see cref="EventKind.Decision"/>'s
    /// own note and the plan's §3.1. A verdict table keyed on event id would record the same fact
    /// one indirection away and drift from it; the one-store rule exists precisely to prevent that.
    ///
    /// Reviewing is IDEMPOTENT per decision because the verdict lives here: a second review of the
    /// same decision overwrites these three fields rather than appending a row, so "reviewed twice"
    /// cannot become "counted twice" in the agreement rate (plan §5).
    /// </summary>
    public ReviewVerdict? ReviewVerdict { get; set; }

    /// <summary>When the verdict was recorded, or null while the decision is unreviewed.</summary>
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>
    /// The reviewer's optional free-text note (#54 AC2's "with an optional note"). Null both when
    /// the decision is unreviewed and when it was reviewed without a note — the distinction that
    /// matters is carried by <see cref="ReviewVerdict"/>, not by this field's nullness.
    ///
    /// Never a credential, on the same footing as <see cref="Detail"/> (plan §9).
    /// </summary>
    public string? ReviewNote { get; set; }
}
