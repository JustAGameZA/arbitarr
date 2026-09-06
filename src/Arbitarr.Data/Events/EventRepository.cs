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

        var filtered = _dbContext.Events.AsNoTracking();

        if (query.Kind is { } kind)
        {
            filtered = filtered.Where(e => e.Kind == kind);
        }

        if (query.Cursor is { } cursor)
        {
            filtered = filtered.Where(e => e.Id < cursor);
        }

        // The kind and cursor predicates translate to SQL, but the OccurredAt comparisons do not:
        // this codebase's SQLite/EF Core combination cannot translate DateTimeOffset comparisons
        // server-side, which is why GetAllAsync and PruneAsync (and MaintenanceJob before them) also
        // compare that column client-side. Taking one row past the page is what reveals whether a
        // further page exists without a second COUNT query.
        var candidates = await filtered.ToListAsync(cancellationToken);

        var page = candidates
            .Where(e => query.Since is not { } since || e.OccurredAt >= since)
            .Where(e => query.Until is not { } until || e.OccurredAt < until)
            .OrderByDescending(e => e.Id)
            .Take(limit + 1)
            .ToList();

        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        // Null on the last page, so a caller stops rather than re-requesting forever.
        var nextCursor = hasMore && page.Count > 0 ? page[^1].Id : (long?)null;

        return new EventPage(page, nextCursor);
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
