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
}
