using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Search;

/// <summary>
/// One release in an ad-hoc search response — <see cref="Arbitarr.Core.Releases.ReleaseCandidate"/>
/// rendered byte-exact (title/size/category/guid untouched, same passthrough contract as the
/// Torznab/Newznab XML rendering, M1-4).
/// </summary>
public sealed record AdHocReleaseResponse(
    string Title,
    string Guid,
    long Size,
    IReadOnlyList<int> Category,
    string SourceName,
    DateTimeOffset PubDate);

/// <summary>
/// Cache/rate-limit provenance for an ad-hoc search response, so the admin dashboard can show
/// whether a result set was served live or from the pagination snapshot cache (AC16), and which
/// upstream sources (if any) were rate-limited while materializing it. Per-release cache-band age
/// is not yet available (that lands with the M5/M6 classifier pipeline) — this strip only reports
/// what <see cref="PaginationSnapshotService"/> already knows today.
/// </summary>
public sealed record AdHocSearchProvenanceResponse(bool FromCache, IReadOnlyList<string> RateLimitedSources);

/// <summary>Response body for <c>GET /api/admin/search</c>.</summary>
public sealed record AdHocSearchResponse(
    IReadOnlyList<AdHocReleaseResponse> Releases,
    AdHocSearchProvenanceResponse Provenance);

/// <summary>
/// M7-1 (non-AI half): admin-gated ad-hoc search for the dashboard. Runs the exact same
/// <see cref="PaginationSnapshotService"/>/<see cref="UpstreamMergeStage"/> path as
/// <c>/torznab/api</c> and <c>/newznab/api</c> — same snapshot cache, same AC16 pagination
/// semantics — but renders JSON instead of Torznab/Newznab XML, since this route feeds the
/// dashboard UI rather than an *arr client.
///
/// <c>tvdbid</c>/<c>tmdbid</c>/<c>season</c>/<c>ep</c> are accepted from the dashboard's query form
/// but M1's <see cref="SearchQuery"/>/<see cref="IUpstreamSource"/> contract has no season/episode-
/// or identifier-aware search capability yet, so they are folded into the free-text query term
/// alongside <c>q</c> (best-effort, same as a human typing them into a search box) rather than
/// silently dropped or used to widen <see cref="SearchQuery"/> — that is a bigger, cross-cutting
/// change out of scope for the "non-AI half" of M7-1.
///
/// The AC14b synchronous-AI-arbitration opt-in is explicitly NOT wired here: it waits on M5, and
/// this endpoint (Arbitarr.Api) must never reference Arbitarr.Ai (AC6a) regardless.
/// </summary>
public static class AdHocSearchEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/admin/search", HandleAsync)
            .RequireAdminApiKey();

    private static async Task<IResult> HandleAsync(
        string? q,
        string? tvdbid,
        string? tmdbid,
        string? season,
        string? ep,
        string? cat,
        int? limit,
        int? offset,
        PaginationSnapshotService snapshotService,
        CancellationToken cancellationToken)
    {
        var queryText = ComposeQueryText(q, tvdbid, tmdbid, season, ep);
        var categories = ParseCategories(cat);
        var query = new SearchQuery(queryText, categories, limit ?? 50, offset ?? 0);

        var result = await snapshotService.GetPageAsync("search", query, cancellationToken).ConfigureAwait(false);

        var releases = result.Releases
            .Select(r => new AdHocReleaseResponse(
                Title: r.Candidate.Title,
                Guid: r.Candidate.Guid,
                Size: r.Candidate.Size,
                Category: r.Candidate.Category,
                SourceName: r.SourceName,
                PubDate: r.Candidate.PubDate))
            .ToArray();

        var response = new AdHocSearchResponse(
            Releases: releases,
            Provenance: new AdHocSearchProvenanceResponse(result.FromCache, result.RateLimitedSources));

        return Results.Ok(response);
    }

    // Best-effort fold of identifier/season/episode form fields into the free-text query term —
    // see the type-level doc comment for why these aren't threaded into SearchQuery itself yet.
    private static string? ComposeQueryText(string? q, string? tvdbid, string? tmdbid, string? season, string? ep)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            parts.Add(q.Trim());
        }

        if (!string.IsNullOrWhiteSpace(tvdbid))
        {
            parts.Add($"tvdbid:{tvdbid.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(tmdbid))
        {
            parts.Add($"tmdbid:{tmdbid.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(season))
        {
            parts.Add($"S{season.Trim().PadLeft(2, '0')}");
        }

        if (!string.IsNullOrWhiteSpace(ep))
        {
            parts.Add($"E{ep.Trim().PadLeft(2, '0')}");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    // Same comma-separated category-list convention as Program.cs's Torznab/Newznab
    // ParseCategories local function (not reusable across files as a local function, so mirrored
    // here rather than introducing a new shared abstraction for this one-line parse).
    private static IReadOnlyList<int> ParseCategories(string? cat) =>
        string.IsNullOrWhiteSpace(cat)
            ? Array.Empty<int>()
            : cat.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => int.TryParse(v, out var id) ? id : (int?)null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray();
}
