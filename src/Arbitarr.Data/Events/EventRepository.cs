using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Events;

/// <summary>
/// The shared event store's persistence (#55 step 1, foundation for #54 — plan §2/§4 item 1). Same
/// validate-at-the-repository-boundary posture as <see cref="Settings.SettingsRepository"/> (AC24 —
/// reject malformed input, never clamp or coerce it into something valid).
///
/// Nothing writes to or reads from this repository outside of these methods and their tests yet.
/// Emission (from the pipeline, worker, snapshot refresh, and source-health paths) is the next
/// stage; <c>GET /api/activity</c> and the Suppressions-surface review affordance are later still.
/// Shipping the store alone first means this PR cannot change runtime behavior even if something in
/// it is wrong — the same posture #53's stage 53a took for source persistence.
/// </summary>
public sealed class EventRepository
{
    /// <summary>
    /// Floor on how many rows one scan batch asks SQLite for (see <see cref="QueryAsync"/>). Only the
    /// time filters can reject a fetched row, so a batch sized to the shortfall alone would degrade
    /// to one round trip per rejected row when a window is sparse. Over-fetching a little amortises
    /// that; the ceiling on total work stays the early exit, not this number.
    /// </summary>
    private const int MinimumScanBatch = 256;

    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public EventRepository(ArbitarrDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates and inserts a new event row. Rejects an empty <paramref name="summary"/> (AC24 —
    /// a row with nothing describing what happened is worse than no row) and a
    /// <paramref name="sourceDisplayName"/> that looks like it might carry a credential rather than
    /// an identity (plan §9) — see <see cref="ValidateSourceDisplayName"/>.
    /// </summary>
    public async Task<EventEntry> AddAsync(
        EventKind kind,
        string summary,
        string? reason,
        string? sourceDisplayName,
        string? detail,
        CancellationToken cancellationToken)
    {
        ValidateSummary(summary);
        ValidateSourceDisplayName(sourceDisplayName);

        var entry = new EventEntry
        {
            Kind = kind,
            OccurredAt = _timeProvider.GetUtcNow(),
            Summary = summary,
            Reason = reason,
            SourceDisplayName = sourceDisplayName,
            Detail = detail,
        };

        _dbContext.Events.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return entry;
    }

    /// <summary>
    /// Validates and inserts several event rows in ONE round trip, for a caller that produces a
    /// burst of related events at a single point (today: <c>FilterStage</c>, which emits one
    /// Decision row per suppressed release and can suppress dozens within one search).
    ///
    /// This exists because the alternative is not merely slower, it is wrong-shaped: N calls to
    /// <see cref="AddAsync"/> mean N SaveChangesAsync round trips awaited in sequence on the path
    /// serving a search, which is exactly what the interface's "must not block the caller's work"
    /// contract forbids. One batch is one transaction, so it is also atomic per burst.
    ///
    /// Validation is unchanged and still per-row: every entry is validated BEFORE anything is
    /// staged, so one credential-shaped display name rejects the whole batch rather than writing a
    /// partial one. Rejecting loudly and completely is the same posture <see cref="AddAsync"/>
    /// takes (AC24 — never coerce malformed input into something valid).
    /// </summary>
    public async Task<IReadOnlyList<EventEntry>> AddRangeAsync(
        IReadOnlyList<(EventKind Kind, string Summary, string? Reason, string? SourceDisplayName, string? Detail)> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return Array.Empty<EventEntry>();
        }

        foreach (var e in events)
        {
            ValidateSummary(e.Summary);
            ValidateSourceDisplayName(e.SourceDisplayName);
        }

        // One timestamp for the whole burst: these events did happen at one point, and reading them
        // back with a spread of microseconds would imply an ordering the caller never had.
        var occurredAt = _timeProvider.GetUtcNow();

        var entries = events
            .Select(e => new EventEntry
            {
                Kind = e.Kind,
                OccurredAt = occurredAt,
                Summary = e.Summary,
                Reason = e.Reason,
                SourceDisplayName = e.SourceDisplayName,
                Detail = e.Detail,
            })
            .ToList();

        _dbContext.Events.AddRange(entries);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return entries;
    }

    /// <summary>All events, most recent first. A test-only read: it materializes the whole table,
    /// so nothing that serves a request may call it. <see cref="QueryAsync"/> is the paged,
    /// filterable read that <c>GET /api/activity</c> uses; this remains only because the retention
    /// and validation tests assert over the complete table by design.</summary>
    public async Task<List<EventEntry>> GetAllAsync(CancellationToken cancellationToken)
    {
        // Ordering by OccurredAt (a DateTimeOffset) cannot be translated server-side by SQLite's EF
        // Core provider (same limitation documented on Maintenance.MaintenanceJob's prune methods),
        // so rows are fetched then sorted client-side.
        var all = await _dbContext.Events.AsNoTracking().ToListAsync(cancellationToken);
        return all.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).ToList();
    }

    /// <summary>
    /// Reads one page of events, most recent first, filtered per <paramref name="query"/> (#55 step
    /// 3 — the read side of the store this class already owns; no new table, column or kind).
    ///
    /// PAGING IS BY SEEK CURSOR, NOT OFFSET, AND THAT IS LOAD-BEARING (AC8). See
    /// <see cref="EventQuery.Cursor"/> for the full reasoning; the short version is that this store
    /// grows at the same end it is read from, so an offset-paged reader re-reads and skips rows
    /// whenever an event arrives mid-page. Anchoring each page to the last <see cref="EventEntry.Id"/>
    /// the caller actually received removes that class of defect by construction, rather than
    /// leaving it to be noticed in production.
    ///
    /// Ordering is by Id, not OccurredAt, and the two are NOT interchangeable here even though the
    /// rows are near-sorted by both. Id is unique and monotonic per insert, so it totally orders the
    /// table and gives the cursor an unambiguous "strictly before this row" boundary. OccurredAt is
    /// neither: several events can share one instant (the worker emits a handful within a cycle),
    /// and a cursor on a non-unique key either drops the tied rows or repeats them. OccurredAt stays
    /// the column the time FILTERS apply to, which is a different job.
    /// </summary>
    public async Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, EventQuery.MaxLimit);

        // Rows wanted before we can answer: the page itself, plus one more that only tells us
        // whether a further page exists (cheaper than a second COUNT query).
        var wanted = limit + 1;

        // THE WORK IS BOUNDED IN SQL, NOT ONLY THE RESPONSE. The kind and cursor predicates, the
        // ordering and the LIMIT all translate and ride the (Kind, OccurredAt) index; the OccurredAt
        // comparisons genuinely CANNOT translate on this provider — EF Core throws rather than
        // degrading, so this is verified, not assumed — which is why GetAllAsync, PruneAsync and
        // MaintenanceJob all compare that column client-side too.
        //
        // A single .ToListAsync() over the kind/cursor-filtered set followed by a client-side Take
        // would therefore be correct but load every matching row: `?since=<a minute ago>` against a
        // large table would materialize the whole table to return a handful, making MaxLimit a cap
        // on the RESPONSE while the work stayed unbounded. That defeats the purpose it documents.
        //
        // So rows are pulled in SQL-bounded batches, newest first, and time-filtered as they arrive.
        // Because the scan runs in descending Id order and Id is monotonic with insertion time, a
        // batch whose newest row already sits before `Since` proves every remaining row does too —
        // that is the early exit that keeps a narrow recent window cheap regardless of table size.
        var page = new List<EventEntry>(wanted);
        var scanCursor = query.Cursor;

        while (page.Count < wanted)
        {
            var batchSize = Math.Max(wanted - page.Count, MinimumScanBatch);

            var batch = await BuildScanQuery(query.Kind, scanCursor)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                // The table is exhausted for this filter.
                break;
            }

            // Resume strictly below the oldest row seen, so batches never overlap or skip.
            scanCursor = batch[^1].Id;

            foreach (var entry in batch)
            {
                if (query.Until is { } until && entry.OccurredAt >= until)
                {
                    // Newer than the window's upper bound. Later rows are older, so this is not a
                    // stopping condition — just skip it and keep descending.
                    continue;
                }

                if (query.Since is { } since && entry.OccurredAt < since)
                {
                    // Past the lower bound. Everything below is older still, so nothing further can
                    // match: return what we have rather than scanning the rest of the table.
                    return BuildPage(page, limit);
                }

                page.Add(entry);
                if (page.Count == wanted)
                {
                    break;
                }
            }

            if (batch.Count < batchSize)
            {
                // A short batch means the table ran out, not that the window did.
                break;
            }
        }

        return BuildPage(page, limit);
    }

    /// <summary>
    /// Prunes rows past their kind's <see cref="EventRetentionPolicy"/> window and returns how many
    /// were removed, grouped by kind so a caller (a future scheduler, or a test) can see the
    /// asymmetric retention actually holding: decisions survive far longer than operational events.
    ///
    /// Filtering is done client-side against <see cref="EventRetentionPolicy"/> rather than
    /// expressed as a SQL WHERE clause, matching <c>MaintenanceJob</c>'s existing precedent for this
    /// codebase's SQLite/EF Core combination (DateTimeOffset comparisons do not reliably translate
    /// server-side) — see e.g. <c>Maintenance.MaintenanceJob.PruneSuppressionAuditLogAsync</c>.
    /// </summary>
    public async Task<IReadOnlyDictionary<EventKind, int>> PruneAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var candidates = await _dbContext.Events.ToListAsync(cancellationToken);

        var prunable = candidates
            .Where(e => now - e.OccurredAt > EventRetentionPolicy.For(e.Kind))
            .ToList();

        var byKind = prunable
            .GroupBy(e => e.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        _dbContext.Events.RemoveRange(prunable);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return byKind;
    }

    /// <summary>
    /// One SQL-bounded descending scan step: kind and cursor as WHERE clauses, Id DESC as ORDER BY.
    /// Every part of this translates to SQL on this provider (verified, not assumed — see
    /// <see cref="QueryAsync"/>), so the caller's <c>.Take()</c> becomes a real LIMIT rather than a
    /// client-side truncation of an already-materialized table.
    /// </summary>
    private IQueryable<EventEntry> BuildScanQuery(EventKind? kind, long? cursor)
    {
        var scan = _dbContext.Events.AsNoTracking();

        if (kind is { } k)
        {
            scan = scan.Where(e => e.Kind == k);
        }

        if (cursor is { } c)
        {
            scan = scan.Where(e => e.Id < c);
        }

        return scan.OrderByDescending(e => e.Id);
    }

    /// <summary>
    /// Turns the accumulated rows into a page. The scan collects up to <c>limit + 1</c> rows; that
    /// extra row is a probe, never part of the response — its presence is the whole signal that a
    /// further page exists. NextCursor is null on the last page so a caller stops rather than
    /// re-requesting forever.
    /// </summary>
    private static EventPage BuildPage(List<EventEntry> collected, int limit)
    {
        var hasMore = collected.Count > limit;
        if (hasMore)
        {
            collected.RemoveAt(collected.Count - 1);
        }

        var nextCursor = hasMore && collected.Count > 0 ? collected[^1].Id : (long?)null;

        return new EventPage(collected, nextCursor);
    }

    private static void ValidateSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new EventValidationException("Event summary must not be empty.");
        }
    }

    private static void ValidateSourceDisplayName(string? sourceDisplayName)
    {
        // AC24 + plan §9: a source is identified by its display name/id, never a credential. This
        // cannot prove a caller never passes a key here, but it rejects the most obvious accidents —
        // a bearer-style token or a query string carrying "key"/"token"/"apikey" — outright rather
        // than accepting anything silently. A source's real display name is short, human-chosen
        // prose and will never incidentally match this shape.
        if (sourceDisplayName is null)
        {
            return;
        }

        if (sourceDisplayName.Length > 256)
        {
            throw new EventValidationException("Source display name is unexpectedly long for an identity string.");
        }

        var lowered = sourceDisplayName.ToLowerInvariant();
        if (lowered.Contains("apikey") || lowered.Contains("api_key") || lowered.Contains("token")
            || sourceDisplayName.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new EventValidationException(
                "Source display name looks like it may contain a credential; pass an identity (display name or id), never a key.");
        }
    }
}
