using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Api.Admin;

/// <summary>
/// One suppressed-or-de-ranked row as served over the wire (P3): the release it applied to, which
/// layer of the <see cref="Arbitarr.Core.Filtering.SuppressionPrecedenceChain"/> acted (the same
/// <c>RuleName</c> attribution <see cref="Arbitarr.Data.Filtering.SuppressionAuditLogMapper"/>
/// already writes — a rule name for the rule-engine layers, or a stable layer label such as
/// <c>"ai"</c>/<c>"pass"</c> for the others), and the human-readable reason recorded at the time.
/// </summary>
public sealed record SuppressionViewEntryResponse(
    DateTimeOffset OccurredAt,
    string ReleaseIdentifier,
    string QueryKey,
    string Layer,
    string Reason,
    bool ShadowMode);

/// <summary>
/// Wave-C item 3 (plan M7 UI list item 3, P3): admin-gated, read-only view over every suppression
/// source's decisions for a query, each attributed to the layer that acted (rule engine
/// allow/deny, AI verdict — the identity layer and numbering scorer do not suppress today and
/// therefore never appear here; see <see cref="Arbitarr.Core.Filtering.SuppressionPrecedenceChain"/>)
/// and its reason. Reads straight from <see cref="SuppressionAuditLogEntry"/> — the append-only log
/// <see cref="Arbitarr.Api.Search.FilterStage"/> already writes one row per suppression to (M4-5) —
/// no new suppression logic runs here, and this endpoint (Arbitarr.Api) must never reference
/// Arbitarr.Ai (AC6a).
/// <para>
/// <see cref="SuppressionViewEntryResponse.Reason"/> is returned as-is from the audit log; no
/// separate reflection bound is applied here. Reason text is already bounded at write time by
/// <c>FilterStage.ClampForReflection</c> (120 chars + "…") once this branch is rebased onto master
/// at or after commit 5af8200, which landed that clamp after this branch's base (67775e4).
/// </para>
/// </summary>
public static class SuppressionViewEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/admin/suppressions", HandleAsync)
            .RequireAdminApiKey();

    public static async Task<IResult> HandleAsync(
        string? queryKey,
        ArbitarrDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SuppressionAuditLogEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(queryKey))
        {
            query = query.Where(e => e.QueryKey == queryKey);
        }

        // SQLite cannot translate ORDER BY on a DateTimeOffset column server-side, so the rows are
        // materialized first and ordered client-side (the audit log is already scoped to one query
        // when queryKey is supplied, so this stays a small, bounded set).
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);

        var entries = rows
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new SuppressionViewEntryResponse(
                e.OccurredAt,
                e.ReleaseIdentifier,
                e.QueryKey,
                e.RuleName,
                e.Reason,
                e.ShadowMode))
            .ToList();

        return Results.Ok(entries);
    }
}
