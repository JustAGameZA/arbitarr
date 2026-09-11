using Arbitarr.Api.Routing;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Dashboard;

/// <summary>
/// One activity row as served over the wire (#55 step 3).
/// </summary>
/// <param name="OccurredAt">
/// When it happened, as a <see cref="DateTimeOffset"/> — serialized in ISO-8601 WITH its offset
/// (e.g. <c>2026-09-07T14:03:11+00:00</c>), which is AC9. The offset is carried rather than dropped
/// deliberately: a bare local-looking timestamp is exactly the ambiguity AC9 exists to prevent, and
/// this follows the same honesty principle as <c>System.tsx</c>'s verbatim durations (plan §4 step 7)
/// — state the actual instant and let the reader localize it, rather than inventing a relative
/// formatter that hides which instant it was.
/// </param>
/// <param name="Kind">The event kind, camelCase (e.g. <c>workerCycle</c>), for the surface's filter.</param>
/// <param name="Summary">One line stating what happened.</param>
/// <param name="Reason">Why it happened, or null when the summary is the whole answer (AC2).</param>
/// <param name="SourceDisplayName">The source involved, if any. Never a credential (plan §9).</param>
/// <param name="Detail">Free-form kind-specific detail, or null.</param>
/// <param name="RepeatCount">
/// How many times this identical event occurred, counting the first (arb-itw). 1 on an ordinary
/// row. Repeats are folded onto one row at WRITE time — see <c>EventCoalescing</c> for why there
/// rather than here — so this count is a stored fact the surface reports, not something the reader
/// recomputes by grouping a page it happens to be holding.
/// </param>
/// <param name="LastRepeatedAt">
/// When it most recently repeated, or null while it has occurred only once. Carried with its offset
/// on the same AC9 footing as <paramref name="OccurredAt"/>, and paired with it: together they say
/// "started at X, still happening at Y", which one timestamp cannot.
/// </param>
public sealed record ActivityEntryResponse(
    DateTimeOffset OccurredAt,
    string Kind,
    string Summary,
    string? Reason,
    string? SourceDisplayName,
    string? Detail,
    int RepeatCount,
    DateTimeOffset? LastRepeatedAt);

/// <summary>
/// One page of activity (#55 step 3).
/// </summary>
/// <param name="Events">The page's rows, most recent first.</param>
/// <param name="NextCursor">
/// Opaque cursor for the next page, or null on the last page. Echoed back as <c>?cursor=</c>.
/// Deliberately not a page number or an offset — see <see cref="EventQuery.Cursor"/> for why that
/// is AC8 rather than a style preference.
/// </param>
public sealed record ActivityPageResponse(IReadOnlyList<ActivityEntryResponse> Events, long? NextCursor);

/// <summary>
/// Maps <c>GET /api/activity</c> (#55 step 3, plan §4 item 3): the paged, kind- and time-filterable
/// read over the shared event store PR #70 shipped.
///
/// GATE CLASSIFICATION — <see cref="RouteClassification.PublicRead"/>, and this is now settled
/// rather than provisional. The plan's §3.2 defaulted here pending #59; #59 has since closed with
/// D2 unamended: the admin key gates MUTATING actions, reading is not mutating, and since #43 a LAN
/// client reaches admin routes without a key anyway, so gating this would cost the Dashboard's
/// consistency (the staleness envelope and recent-searches reads are un-gated) while buying nothing.
/// Classification is by <see cref="RouteClassification"/>, never by HTTP verb.
///
/// What this endpoint may therefore serve is bounded by what the store may hold: no row carries a
/// credential by construction (there is no secret-shaped column, and the repository rejects a
/// credential-shaped source display name at the write boundary), which is what makes an un-gated
/// read of it safe rather than merely conventional.
/// </summary>
public static class ActivityEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/activity", HandleAsync)
            .WithClassification(RouteClassification.PublicRead);

    /// <param name="kind">
    /// Optional kind filter, matched case-insensitively against <see cref="EventKind"/>. An
    /// unrecognized value is a 400 rather than being ignored: silently serving every kind when the
    /// caller asked for one would answer a question they did not ask.
    /// </param>
    public static async Task<IResult> HandleAsync(
        string? kind,
        DateTimeOffset? since,
        DateTimeOffset? until,
        long? cursor,
        int? limit,
        EventRepository repository,
        CancellationToken cancellationToken)
    {
        EventKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse<EventKind>(kind, ignoreCase: true, out var value) || !Enum.IsDefined(value))
            {
                // Enum.TryParse accepts a bare numeric string for any value, defined or not, so
                // Enum.IsDefined is the half that actually rejects "?kind=99".
                return Results.BadRequest(new
                {
                    error = $"Unknown event kind '{kind}'. Expected one of: {string.Join(", ", Enum.GetNames<EventKind>())}.",
                });
            }

            parsedKind = value;
        }

        var page = await repository.QueryAsync(
            new EventQuery(
                Kind: parsedKind,
                Since: since,
                Until: until,
                Cursor: cursor,
                Limit: limit ?? EventQuery.DefaultLimit),
            cancellationToken).ConfigureAwait(false);

        var entries = page.Events
            .Select(e => new ActivityEntryResponse(
                e.OccurredAt,
                ToWireKind(e.Kind),
                e.Summary,
                e.Reason,
                e.SourceDisplayName,
                e.Detail,
                e.RepeatCount,
                e.LastRepeatedAt))
            .ToList();

        return Results.Ok(new ActivityPageResponse(entries, page.NextCursor));
    }

    /// <summary>
    /// The camelCase wire name for a kind, matching how the rest of this API serializes names and
    /// what the Activity surface's filter sends back.
    /// </summary>
    private static string ToWireKind(EventKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
