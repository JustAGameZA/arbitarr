using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Dashboard;

/// <summary>
/// One reviewable pipeline decision as served over the wire (#54 step 3).
/// </summary>
/// <param name="Id">
/// The event row's id, and what <c>POST /api/admin/decisions/{id}/review</c> takes. A real row
/// identity rather than a synthesized handle: the verdict is stored ON this row (plan §3.1), so
/// there is nothing else for it to be.
/// </param>
/// <param name="OccurredAt">
/// When the decision was made, ISO-8601 with its offset — the same honesty principle
/// <see cref="ActivityEntryResponse"/> follows: state the instant, let the reader localize it.
/// </param>
/// <param name="Summary">One line stating what the pipeline did.</param>
/// <param name="Reason">Why it did it, as recorded at decision time (AC1's "and why").</param>
/// <param name="Detail">Which layer acted and on which release. Never a credential (plan §9).</param>
/// <param name="ShadowMode">
/// Whether the pipeline was in shadow mode WHEN THIS DECISION WAS MADE (AC1) — read from the stored
/// flag, never from the current setting, so flipping the switch does not retroactively relabel
/// history.
/// </param>
/// <param name="ReviewVerdict">
/// <c>agree</c>, <c>disagree</c>, or null when nobody has reviewed this decision yet. Null is the
/// unreviewed state rather than a third verdict name — see <see cref="Data.Entities.ReviewVerdict"/>.
/// </param>
/// <param name="ReviewedAt">When the verdict was recorded, or null while unreviewed.</param>
/// <param name="ReviewNote">The reviewer's optional note, or null.</param>
public sealed record DecisionEntryResponse(
    long Id,
    DateTimeOffset OccurredAt,
    string Summary,
    string? Reason,
    string? Detail,
    bool? ShadowMode,
    string? ReviewVerdict,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

/// <summary>One page of decisions (#54 step 3).</summary>
/// <param name="Decisions">The page's rows, most recent first.</param>
/// <param name="NextCursor">
/// Opaque cursor for the next page, or null on the last page. A seek cursor, not an offset — see
/// <see cref="EventQuery.Cursor"/> for why that is load-bearing rather than stylistic.
/// </param>
public sealed record DecisionPageResponse(IReadOnlyList<DecisionEntryResponse> Decisions, long? NextCursor);

/// <summary>
/// The agreement counts over a window (#54 step 5 / AC3).
/// </summary>
/// <param name="Agreed">Reviewed decisions the operator marked correct.</param>
/// <param name="Disagreed">Reviewed decisions the operator marked wrong.</param>
/// <param name="Reviewed">The two summed — the denominator of the agreement rate.</param>
/// <param name="WindowDays">How many days back the counts cover, echoed so the caller can label them.</param>
public sealed record DecisionAgreementResponse(int Agreed, int Disagreed, int Reviewed, int WindowDays);

/// <summary>The body of a review (#54 step 4 / AC2).</summary>
/// <param name="Verdict">
/// <c>agree</c> or <c>disagree</c>, case-insensitive. Required: a review with no verdict is not a
/// review, and defaulting one would put a judgement the operator never made into the agreement rate.
/// </param>
/// <param name="Note">The reviewer's optional free-text note.</param>
public sealed record ReviewDecisionRequest(string? Verdict, string? Note);

/// <summary>
/// #54's review queue: the decisions read (step 3), the review write (step 4), and the agreement
/// aggregate (step 5), over the SHARED EVENT STORE — <see cref="EventKind.Decision"/> rows, with the
/// verdict as nullable columns on those same rows. No second table and no parallel query path: the
/// read goes through <see cref="EventRepository.QueryAsync"/>, which #55 built (plan §3.1).
///
/// GATE CLASSIFICATION, WHICH IS TWO DIFFERENT ANSWERS ON PURPOSE:
///
/// - The READS (<c>/api/decisions</c>, <c>/api/decisions/agreement</c>) are
///   <see cref="RouteClassification.PublicRead"/>. This is settled, not provisional, and does not
///   need re-litigating: #59 closed with its D2 ruling unamended — the admin key gates MUTATING
///   actions, and reading is not mutating — and <see cref="ActivityEndpoint"/> set the precedent by
///   serving this very store un-gated. Gating these two while <c>/api/activity</c> reads the same
///   rows would be an inconsistency that buys nothing, since a LAN client reaches admin routes
///   without a key anyway (#43). What they may serve is bounded by what the store may hold, and no
///   row carries a credential by construction.
/// - The REVIEW WRITE is <see cref="RouteClassification.AdminMutating"/> under the
///   <c>/api/admin/</c> prefix (AC7). It records operator judgement durably, which is a mutation.
///
/// Classification is by <see cref="RouteClassification"/> — never by HTTP verb. The read being a GET
/// and the write a POST is a consequence of what they do, not the reason either is classified as it
/// is; verb-based gating would silently break the GET-only surfaces.
/// </summary>
public static class DecisionReviewEndpoints
{
    /// <summary>
    /// The review route, as a template. Named here because
    /// <c>AdminApiKeyTemplatedRouteTests</c> asserts this exact route is gated: the enumeration
    /// sweep skips <c>{id}</c>-templated routes by design, so a templated admin route that nothing
    /// names by hand is a route nothing checks.
    /// </summary>
    public const string ReviewRoute = "/api/admin/decisions/{id:long}/review";

    /// <summary>
    /// Default window for the agreement aggregate. A week, because AC3's motivating sentence is
    /// "the pipeline has been right 47 of 52 times THIS WEEK" — the figure an operator weighs when
    /// deciding whether to leave shadow mode. Deliberately NOT the same figure as
    /// <see cref="EventRetentionPolicy.DecisionRetention"/> (180 days) and not a competing retention
    /// setting: retention says how long a row survives, this says how much recent history the rate
    /// summarizes. The longer retention exists precisely so this shorter window always has a
    /// populated sample to draw from.
    /// </summary>
    public const int DefaultAgreementWindowDays = 7;

    /// <summary>Largest agreement window a caller may ask for, bounded by how long rows survive.</summary>
    public static int MaxAgreementWindowDays => (int)EventRetentionPolicy.DecisionRetention.TotalDays;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/decisions", ListAsync)
            .WithClassification(RouteClassification.PublicRead);

        endpoints.MapGet("/api/decisions/agreement", GetAgreementAsync)
            .WithClassification(RouteClassification.PublicRead);

        endpoints.MapPost(ReviewRoute, ReviewAsync)
            .RequireAdminApiKey();
    }

    /// <param name="shadowMode">
    /// Optional filter: <c>true</c> for decisions made under shadow mode, <c>false</c> for those
    /// made under live enforcement, omitted for both (#54 step 3). Applied against the flag stored
    /// at decision time, so the two runs stay distinguishable after the switch is flipped.
    /// </param>
    public static async Task<IResult> ListAsync(
        bool? shadowMode,
        long? cursor,
        int? limit,
        EventRepository repository,
        CancellationToken cancellationToken)
    {
        var page = await repository.QueryAsync(
            new EventQuery(
                // Pinned to Decision: this surface reviews pipeline decisions, and admitting the
                // operational kinds would offer a verdict on "the worker ran a cycle".
                Kind: EventKind.Decision,
                Cursor: cursor,
                Limit: limit ?? EventQuery.DefaultLimit,
                ShadowMode: shadowMode),
            cancellationToken).ConfigureAwait(false);

        var decisions = page.Events.Select(ToResponse).ToList();

        return Results.Ok(new DecisionPageResponse(decisions, page.NextCursor));
    }

    /// <param name="windowDays">
    /// How many days back to count, defaulting to <see cref="DefaultAgreementWindowDays"/>. Rejected
    /// rather than clamped when out of range (AC24's reject-never-clamp): silently answering for a
    /// different window than the caller asked for makes the number mean something other than its
    /// own label.
    /// </param>
    public static async Task<IResult> GetAgreementAsync(
        int? windowDays,
        EventRepository repository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var window = windowDays ?? DefaultAgreementWindowDays;

        if (window < 1 || window > MaxAgreementWindowDays)
        {
            return Results.BadRequest(new
            {
                error = $"windowDays must be between 1 and {MaxAgreementWindowDays} "
                    + "(the decision retention window; nothing older survives to be counted).",
            });
        }

        var since = timeProvider.GetUtcNow() - TimeSpan.FromDays(window);
        var agreement = await repository.GetAgreementAsync(since, cancellationToken).ConfigureAwait(false);

        // Counts, never a rate. With zero reviews there is no rate to state -- 0/0 is not 0%, which
        // is AC4 -- so the ratio is formed at the point of display, where "no data yet" already has
        // an established rendering (formatRate's em-dash in System.tsx). Returning 0 here would
        // assert a measured zero agreement rate: a different, and much wronger, claim than "nobody
        // has reviewed anything yet".
        return Results.Ok(new DecisionAgreementResponse(
            agreement.Agreed,
            agreement.Disagreed,
            agreement.Reviewed,
            window));
    }

    /// <param name="request">
    /// BOUND OPTIONALLY, AND THAT IS A SECURITY PROPERTY, NOT A STYLE CHOICE. Model binding runs
    /// BEFORE endpoint filters, so a REQUIRED body would short-circuit to 400 without
    /// <see cref="AdminApiKeyFilter"/> ever executing — letting an unauthenticated remote caller tell
    /// a malformed body (400) from a well-formed one (503) and thereby probe past the gate. Binding
    /// it optionally and null-checking inside the handler keeps the gate strictly first, so an
    /// unauthorised caller learns only that they are unauthorised. Same reasoning, same fix, as
    /// <see cref="AdminSecurityEndpoints"/>, which is where this leak was first found.
    /// </param>
    public static async Task<IResult> ReviewAsync(
        long id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReviewDecisionRequest? request,
        EventRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with a 'verdict' property is required." });
        }

        if (!TryParseVerdict(request.Verdict, out var verdict))
        {
            return Results.BadRequest(new
            {
                error = $"Unknown verdict '{request.Verdict}'. Expected one of: "
                    + $"{string.Join(", ", Enum.GetNames<ReviewVerdict>().Select(n => n.ToLowerInvariant()))}.",
            });
        }

        EventEntry? reviewed;
        try
        {
            reviewed = await repository.ReviewAsync(id, verdict, request.Note, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EventValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // Null means no Decision row with this id. A 404 rather than a silent success: telling the
        // operator their verdict was recorded when no row took it would be the worse failure.
        return reviewed is null
            ? Results.NotFound(new { error = $"No decision with id {id}." })
            : Results.Ok(ToResponse(reviewed));
    }

    /// <summary>
    /// Parses the wire verdict, case-insensitively. Rejects a bare numeric string (which
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would otherwise accept for any
    /// value, defined or not) — <see cref="Enum.IsDefined"/> is the half that actually rejects
    /// <c>"7"</c>.
    /// </summary>
    private static bool TryParseVerdict(string? value, out ReviewVerdict verdict)
    {
        verdict = default;

        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out verdict)
            && Enum.IsDefined(verdict);
    }

    private static DecisionEntryResponse ToResponse(EventEntry entry) => new(
        entry.Id,
        entry.OccurredAt,
        entry.Summary,
        entry.Reason,
        entry.Detail,
        entry.ShadowMode,
        ToWireVerdict(entry.ReviewVerdict),
        entry.ReviewedAt,
        entry.ReviewNote);

    /// <summary>
    /// The camelCase wire name for a verdict, matching how the rest of this API serializes names.
    /// Null stays null: unreviewed is the absence of a verdict, not a third name.
    /// </summary>
    private static string? ToWireVerdict(ReviewVerdict? verdict)
    {
        if (verdict is not { } value)
        {
            return null;
        }

        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
