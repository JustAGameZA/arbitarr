using Arbitarr.Data.Entities;

namespace Arbitarr.Data.Events;

/// <summary>
/// The filters and page position for one <c>GET /api/activity</c> read (#55 step 3, plan §4 item 3
/// — "paged, filterable by kind and time window").
///
/// This is a READ shape over the store PR #70 shipped. It adds no column, no table and no second
/// store: <see cref="EventRepository.QueryAsync"/> translates it against the existing
/// <c>(Kind, OccurredAt)</c> index that <c>ArbitarrDbContext</c> already declares for exactly these
/// two access patterns.
/// </summary>
/// <param name="Kind">
/// Restrict to one <see cref="EventKind"/>, or null for every kind. A single optional kind rather
/// than a set: the surface's filter is a one-of dropdown, and a multi-select nobody asked for would
/// be a wider API to keep honest for no gained answer.
/// </param>
/// <param name="Since">Only events at or after this instant, or null for no lower bound.</param>
/// <param name="Until">Only events strictly before this instant, or null for no upper bound.</param>
/// <param name="Cursor">
/// Where to resume, as the <see cref="EventEntry.Id"/> of the last row the caller already has;
/// the next page starts strictly below it. Null starts at the newest row.
///
/// THIS IS A SEEK CURSOR, NOT AN OFFSET, AND THAT IS AC8 (plan §5: "paging is stable under
/// insertion — a new event arriving mid-page must not cause a row to be skipped or repeated").
/// An offset counts rows from the top of a result set that is growing at the top: insert one event
/// between page 1 and page 2 and every subsequent offset points one row earlier than the caller
/// expects, so the row that was last on page 1 arrives again as first on page 2, and one row is
/// eventually skipped entirely. Anchoring to a row IDENTITY instead makes the boundary immune to
/// what arrives above it — the pathology cannot be expressed, rather than being tested for and
/// hoped about. Do not "simplify" this back to skip/take.
/// </param>
/// <param name="Limit">Maximum rows to return. Clamped by <see cref="EventRepository"/>.</param>
public sealed record EventQuery(
    EventKind? Kind = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    long? Cursor = null,
    int Limit = EventQuery.DefaultLimit)
{
    /// <summary>Page size when a caller does not ask for one.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// Largest page a caller may request. A ceiling exists because this endpoint is
    /// <c>PublicRead</c> (plan §3.2): an unbounded <c>limit</c> would let any LAN caller ask for
    /// the entire 180-day decision history in one query and materialize it in host memory.
    /// </summary>
    public const int MaxLimit = 200;
}

/// <summary>
/// One page of activity, plus the cursor that fetches the next (#55 step 3).
/// </summary>
/// <param name="Events">The page's rows, most recent first.</param>
/// <param name="NextCursor">
/// Pass back as <see cref="EventQuery.Cursor"/> for the following page, or null when this page is
/// the last one. An opaque row position, deliberately not an offset — see
/// <see cref="EventQuery.Cursor"/>.
/// </param>
public sealed record EventPage(IReadOnlyList<EventEntry> Events, long? NextCursor);
