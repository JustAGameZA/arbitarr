using System.Security.Cryptography;
using System.Text;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Admin;

/// <summary>
/// D2: gates every <see cref="Arbitarr.Api.Routing.RouteClassification.AdminMutating"/> endpoint
/// behind <c>SettingKey.AdminApiKey</c>. The admin UI itself has no login of its own — this filter
/// is the entire auth surface for mutating admin routes; read-only lite routes
/// (<see cref="Arbitarr.Api.Routing.RouteClassification.PublicRead"/>) are never wrapped by this
/// filter and stay ungated, per D2.
///
/// Expects the key in an <c>X-Admin-Api-Key</c> request header (never a query string, so it does
/// not end up in access logs or browser history the way the Torznab/Newznab client apikey does).
/// Fails closed: if no admin key has been configured yet (fresh install, before the operator sets
/// one via the settings UI), every admin-mutating request is rejected with 503, not allowed
/// through — an unset gate must never be treated as "no gate needed".
/// </summary>
public sealed class AdminApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Api-Key";

    private readonly IAdminApiKeyReader _keyReader;

    public AdminApiKeyFilter(IAdminApiKeyReader keyReader)
    {
        _keyReader = keyReader;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredKey = await _keyReader.GetCurrentKeyAsync(context.HttpContext.RequestAborted);
        if (string.IsNullOrEmpty(configuredKey))
        {
            return Results.Problem(
                title: "Admin API key not configured",
                detail: "No admin API key has been set yet. Configure one in the settings UI before using admin-mutating endpoints.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var providedKey = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!IsMatch(providedKey, configuredKey))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    // Fixed-time comparison (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals),
    // matching the convention already established by ConfiguredClientApiKeyResolver, so response
    // timing cannot be used to narrow down the admin key.
    private static bool IsMatch(string providedKey, string configuredKey)
    {
        if (string.IsNullOrEmpty(providedKey))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);

        return providedBytes.Length == configuredBytes.Length
            && CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }
}
