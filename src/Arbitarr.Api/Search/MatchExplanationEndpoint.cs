using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Search;

/// <summary>
/// Response body for <c>GET /api/admin/search/{proxyGuid}/explanation</c> (AC-M7b): the
/// match-explanation view for a single release, showing the upstream-reported title side by side
/// with the (possibly rewritten) title actually used for matching, so an operator auditing a wrong
/// match can see at a glance whether normalization altered what the source sent.
/// </summary>
/// <param name="Title">
/// The title as used for matching — normalized, if <c>Arbitarr.Ai</c>'s title normalizer
/// ran (M5); otherwise identical to <paramref name="OriginalTitle"/>.
/// </param>
/// <param name="OriginalTitle">
/// The title exactly as the upstream source reported it, before any normalization
/// (<see cref="Arbitarr.Core.Releases.ReleaseCandidate.OriginalTitle"/>, RX-5). Always populated —
/// equal to <paramref name="Title"/> when no normalization has been applied to this release.
/// </param>
public sealed record MatchExplanationResponse(string Title, string OriginalTitle);

/// <summary>
/// M7-9 (AC26a UI half): admin-gated match-explanation lookup for the dashboard. Resolves a
/// previously rendered release by its <see cref="Arbitarr.Api.Rendering.RenderedRelease.ProxyGuid"/>
/// (the same id space <see cref="DownloadProxyEndpoint"/> and <see cref="SearchEndpoint"/> already
/// use — no new identifier scheme) via <see cref="IReleaseLookup"/>, and returns just the
/// original-vs-rewritten title pair (AC-M7b). This is a pure read: no AI logic runs here and this
/// endpoint (Arbitarr.Api) must never reference Arbitarr.Ai (AC6a).
/// </summary>
public static class MatchExplanationEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        // #58: a pure read over an already-served release, so a ReadOnly-scoped key reaches it,
        // matching AdHocSearchEndpoint — an operator auditing why a match happened should not need
        // a credential that can also rewrite the rules that produced it.
        endpoints.MapGet("/api/admin/search/{proxyGuid}/explanation", HandleAsync)
            .RequireAdminApiKey(ApiKeyScope.ReadOnly);

    public static async Task<IResult> HandleAsync(
        string proxyGuid,
        IReleaseLookup releaseLookup,
        CancellationToken cancellationToken)
    {
        var release = await releaseLookup.FindAsync(proxyGuid, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return Results.NotFound();
        }

        var response = new MatchExplanationResponse(
            Title: release.Candidate.Title,
            OriginalTitle: release.Candidate.OriginalTitle);

        return Results.Ok(response);
    }
}
