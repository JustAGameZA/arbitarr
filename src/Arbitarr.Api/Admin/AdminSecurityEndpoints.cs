using Arbitarr.Core.Settings;
using Arbitarr.Data.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>Request body for <c>PUT /api/admin/security/admin-key</c>.</summary>
public sealed record UpdateAdminApiKeyRequest(string Value);

/// <summary>
/// #43: the one write path for <see cref="SettingKey.AdminApiKey"/>, and deliberately its own
/// surface rather than an entry on the catalog-driven settings surface.
///
/// WHY NOT <see cref="AdminSettingsEndpoints"/>. <c>SettingsCatalog</c> feeds both that route's PUT
/// allow-list and its GET projection, so adding the admin key there to make it writable would also
/// publish it on the read surface — the precise outcome the catalog's exclusion comment exists to
/// prevent, and the same allow-list-only discipline <c>ConfigProjection</c> applies to sensitive
/// values. Hence a separate route: writable here, still absent from the catalog, so
/// <c>PUT /api/admin/settings/AdminApiKey</c> remains a 404 and <c>GET /api/admin/settings</c>
/// never carries the value.
///
/// WRITE-ONLY BY CONSTRUCTION. There is no GET on this route and no projection of the key anywhere:
/// once set it can be replaced but never read back over the API. A 204 carries no body, so not even
/// an echo of the accepted value leaves the process.
///
/// Classified <see cref="Arbitarr.Api.Routing.RouteClassification.AdminMutating"/> like every other
/// admin route, so on a fresh install it is reachable only through
/// <see cref="AdminApiKeyFilter"/>'s local-network bootstrap bypass, and once a key exists it
/// requires that key like everything else. The bypass and this route are the two halves of the #43
/// fix — neither resolves the deadlock alone.
/// </summary>
public static class AdminSecurityEndpoints
{
    public const string AdminKeyRoute = "/api/admin/security/admin-key";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut(AdminKeyRoute, SetAdminApiKeyAsync)
            .RequireAdminApiKey();
    }

    // The body is bound as OPTIONAL ([FromBody(EmptyBodyBehavior.Allow)]) and null-checked here
    // rather than being declared required. A required body is bound BEFORE endpoint filters run, so
    // a missing or malformed one short-circuits to 400 without AdminApiKeyFilter ever executing —
    // which would let an unauthenticated remote caller probe this route and tell a malformed body
    // (400) from a well-formed one (503). Binding it optionally keeps the gate strictly first, so
    // an unauthorised caller learns only that they are unauthorised. Caught by
    // AdminApiKeyRouteEnumerationTests, which sweeps every concrete admin-mutating route with no
    // body at all; this is the first concrete (non-templated) admin route to take one.
    private static async Task<IResult> SetAdminApiKeyAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateAdminApiKeyRequest? request,
        SettingsRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with a 'value' property is required." });
        }

        try
        {
            // Validation lives in SettingsValidator.ValidateAdminApiKey, reached through the
            // repository's own switch — the same AC24 reject-never-clamp path every other setting
            // takes. Not duplicated here: one floor, in one place, already tested.
            await repository.SetAsync(SettingKey.AdminApiKey, request.Value, cancellationToken);
        }
        catch (SettingsValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.NoContent();
    }
}
