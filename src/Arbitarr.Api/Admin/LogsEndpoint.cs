using Arbitarr.Data.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>One log row as served over the wire (#65). Mirrors <see cref="LogEntry"/>.</summary>
public sealed record LogEntryResponse(
    long Id,
    DateTimeOffset Time,
    string Level,
    string Logger,
    string Message,
    string? Exception,
    string? ExceptionType);

/// <summary>Response body of <c>GET /api/admin/logs</c>.</summary>
/// <param name="Entries">The rows on this page, newest first.</param>
/// <param name="Total">Total rows matching the filters across all pages.</param>
/// <param name="Page">The 1-based page actually served (clamped from the request).</param>
/// <param name="PageSize">The page size actually served (clamped from the request).</param>
/// <param name="Loggers">
/// Every distinct logger category present in the store, so the UI can populate its logger filter
/// without a second round trip. Returned with each page rather than from its own endpoint because
/// it is small, and because a filter whose options arrive on a separate request can render empty
/// while the table beside it already shows rows from those very loggers.
/// </param>
public sealed record LogsResponse(
    IReadOnlyList<LogEntryResponse> Entries,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> Loggers);

/// <summary>
/// Maps <c>GET /api/admin/logs</c> (#65 plan §4 item 5): the paged, filterable read behind the
/// System page's Logs tab.
///
/// CLASSIFICATION — ADMIN-GATED, AND THIS IS NOT AN INCONSISTENCY WITH <c>/api/activity</c>.
/// Read this before "fixing" the apparent mismatch. <c>/api/activity</c> (#55/#76) is
/// <see cref="Arbitarr.Api.Routing.RouteClassification.PublicRead"/> under D2, and #59's D2 ruling
/// stands unamended. This route is deliberately gated anyway, because the two surfaces differ in
/// kind and not merely in degree:
///
///   - <c>/api/activity</c> serves DOMAIN events — a closed set of kinds, with fields the
///     application deliberately composed for an operator to read. What it can disclose was decided
///     at each emission site.
///   - This serves RAW APPLICATION LOGS: arbitrary exception text, stack traces, file-system paths,
///     internal type and configuration names, and third-party library output. Nobody vetted that
///     text for an audience, because most of it is written by code that has no idea it is being
///     published. <see cref="LogMessageCleanser"/> reduces that surface but explicitly cannot be
///     relied on as the boundary (see its own remarks — a denylist only catches known shapes).
///
/// Gating the broader, unvetted surface while leaving the narrow, curated one public is the
/// consistent position; treating them identically because both are "read-only" would be the
/// inconsistency. The mechanism follows <see cref="Arbitarr.Api.Dashboard.ObservabilityEndpoint"/> exactly — the
/// <c>/api/admin/</c> path prefix plus <see cref="AdminEndpointConventions.RequireAdminApiKey(Microsoft.AspNetCore.Builder.RouteHandlerBuilder)"/> —
/// which is also what makes the frontend attach the key, since <c>client.ts</c>'s
/// <c>needsAdminKey</c> decides by path prefix and never by verb.
///
/// The <c>AdminMutating</c> classification this carries reads, for a GET, as "gated by the admin
/// key" rather than as a claim that the handler writes anything; it is the same shape
/// <see cref="Arbitarr.Api.Dashboard.ObservabilityEndpoint"/>, <see cref="SuppressionViewEndpoint"/> and the admin search
/// endpoints already use, and <c>AdminApiKeyRouteEnumerationTests</c> asserts that every route so
/// classified is genuinely gated. Introducing a third classification value for "gated read" would
/// mean revisiting that sweep's bidirectional contract for no behavioural gain.
///
/// This route takes NO request body, so the optional-binding rule that
/// <see cref="AdminSecurityEndpoints"/> follows does not apply — but note WHY it does not: the
/// enumeration sweep sends no body on purpose, and a route with a REQUIRED body short-circuits to
/// 400 in model binding before <see cref="AdminApiKeyFilter"/> ever runs, leaking 400-vs-503 past
/// the gate. Query-string parameters are bound optionally by nature, so this route is safe by
/// construction; if it ever grows a body, bind it with <c>EmptyBodyBehavior.Allow</c>.
/// </summary>
public static class LogsEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/admin/logs", HandleAsync).RequireAdminApiKey();

    public static async Task<IResult> HandleAsync(
        string? level,
        string? logger,
        int? page,
        int? pageSize,
        LogStore store,
        CancellationToken cancellationToken)
    {
        var requestedPage = Math.Max(1, page ?? 1);
        var requestedPageSize = Math.Clamp(pageSize ?? LogStore.DefaultPageSize, 1, LogStore.MaxPageSize);

        var result = await store.ReadAsync(level, logger, requestedPage, requestedPageSize, cancellationToken);
        var loggers = await store.GetLoggersAsync(cancellationToken);

        return Results.Ok(new LogsResponse(
            Entries: result.Entries
                .Select(e => new LogEntryResponse(e.Id, e.Time, e.Level, e.Logger, e.Message, e.Exception, e.ExceptionType))
                .ToList(),
            Total: result.Total,
            Page: requestedPage,
            PageSize: requestedPageSize,
            Loggers: loggers));
    }
}
