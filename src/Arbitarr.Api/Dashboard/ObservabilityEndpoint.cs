using Arbitarr.Api.Admin;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Api.Dashboard;

/// <summary>
/// Metadata-cache coverage, as reported by <c>/api/admin/observability</c> (M7 step 7): how much
/// of the identity/numbering metadata the pipeline depends on is currently held locally.
/// </summary>
/// <param name="Entries">Every row in the metadata cache, positive and negative.</param>
/// <param name="NegativeEntries">Rows recording a "looked up, nothing there" result.</param>
/// <param name="DistinctSeries">Distinct series keys with at least one entry of either kind.</param>
public sealed record MetadataCacheCoverage(long Entries, long NegativeEntries, long DistinctSeries);

/// <summary>Response body of <c>GET /api/admin/observability</c>.</summary>
/// <param name="Counters">Process-lifetime pipeline counters (reset on restart).</param>
/// <param name="MetadataCache">Persistent metadata-cache coverage, read from the store on each request.</param>
public sealed record ObservabilityResponse(ObservabilitySnapshot Counters, MetadataCacheCoverage MetadataCache);

/// <summary>
/// Maps <c>GET /api/admin/observability</c> (M7 step 7). Admin-gated (D2): the suppression
/// breakdown names filter rules and the served-age distribution reveals caching behaviour, neither
/// of which belongs on the unauthenticated read surface.
/// </summary>
public static class ObservabilityEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/admin/observability", HandleAsync).RequireAdminApiKey();

    private static async Task<ObservabilityResponse> HandleAsync(
        ObservabilityCounters counters,
        ArbitarrDbContext db,
        CancellationToken cancellationToken)
    {
        var entries = await db.MetadataCacheEntries.LongCountAsync(cancellationToken);
        var negative = await db.MetadataCacheEntries.LongCountAsync(e => e.IsNegative, cancellationToken);
        var series = await db.MetadataCacheEntries.Select(e => e.SeriesKey).Distinct().LongCountAsync(cancellationToken);

        return new ObservabilityResponse(counters.Snapshot(), new MetadataCacheCoverage(entries, negative, series));
    }
}
