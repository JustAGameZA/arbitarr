using Arbitarr.Api.Rendering;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Search;

/// <summary>
/// Validates the inbound Torznab/Newznab client <c>apikey</c> query parameter against the
/// configured expected value (M1-9). This is distinct from any upstream source's own API key
/// (e.g. NZBHydra2's) — it authenticates the *arr client calling into Arbitarr, not Arbitarr
/// calling out to a source. A missing or mismatched key renders the standard
/// <c>&lt;error code="100" .../&gt;</c> element in the correct wrapper for the requesting
/// protocol family, applied uniformly to both <c>t=caps</c> and search requests.
/// </summary>
public static class ApiKeyValidator
{
    /// <summary>Torznab/Newznab error code for "incorrect user credentials" (apikey missing or wrong).</summary>
    public const int InvalidApiKeyErrorCode = 100;

    /// <summary>
    /// Returns <c>null</c> when <paramref name="providedApiKey"/> matches <paramref name="expectedApiKey"/>.
    /// Otherwise returns the rendered error <see cref="IResult"/> for the given protocol family.
    /// </summary>
    public static IResult? Validate(string? providedApiKey, string expectedApiKey, bool isTorznab)
    {
        if (!string.IsNullOrEmpty(expectedApiKey) &&
            string.Equals(providedApiKey, expectedApiKey, StringComparison.Ordinal))
        {
            return null;
        }

        var xml = isTorznab
            ? TorznabXmlWriter.WriteError(InvalidApiKeyErrorCode, "Incorrect user credentials")
            : NewznabXmlWriter.WriteError(InvalidApiKeyErrorCode, "Incorrect user credentials");
        var contentType = isTorznab ? TorznabXmlWriter.ContentType : NewznabXmlWriter.ContentType;

        return Results.Text(XmlDocumentRendering.ToXmlString(xml), contentType);
    }
}
