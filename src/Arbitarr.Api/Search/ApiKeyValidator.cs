using Arbitarr.Api.Rendering;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Search;

/// <summary>
/// Validates the inbound Torznab/Newznab client <c>apikey</c> query parameter via the injected
/// <see cref="IClientApiKeyResolver"/> (M1-9, security-hardened). This is distinct from any
/// upstream source's own API key (e.g. NZBHydra2's) — it authenticates the *arr client calling
/// into Arbitarr, not Arbitarr calling out to a source. A missing or mismatched key renders the
/// standard <c>&lt;error code="100" .../&gt;</c> element in the correct wrapper for the requesting
/// protocol family, applied uniformly to both <c>t=caps</c> and search requests.
/// </summary>
public static class ApiKeyValidator
{
    /// <summary>Torznab/Newznab error code for "incorrect user credentials" (apikey missing or wrong).</summary>
    public const int InvalidApiKeyErrorCode = 100;

    /// <summary>
    /// Resolves <paramref name="providedApiKey"/> via <paramref name="resolver"/>. Returns the
    /// matched <see cref="ClientKeyContext"/> on success; otherwise renders the error
    /// <see cref="IResult"/> for the given protocol family and returns <c>null</c> for the context.
    /// </summary>
    public static (ClientKeyContext? Context, IResult? Error) Validate(
        string? providedApiKey, IClientApiKeyResolver resolver, bool isTorznab)
    {
        var context = resolver.Resolve(providedApiKey);
        if (context is not null)
        {
            return (context, null);
        }

        var xml = isTorznab
            ? TorznabXmlWriter.WriteError(InvalidApiKeyErrorCode, "Incorrect user credentials")
            : NewznabXmlWriter.WriteError(InvalidApiKeyErrorCode, "Incorrect user credentials");
        var contentType = isTorznab ? TorznabXmlWriter.ContentType : NewznabXmlWriter.ContentType;

        return (null, Results.Text(XmlDocumentRendering.ToXmlString(xml), contentType));
    }
}
