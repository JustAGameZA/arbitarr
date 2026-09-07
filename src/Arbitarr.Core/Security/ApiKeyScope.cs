namespace Arbitarr.Core.Security;

/// <summary>
/// #58: what a named API key is allowed to do. Deliberately TWO values, not a permission matrix.
///
/// The split mirrors <c>Arbitarr.Api.Routing.RouteClassification</c> exactly — the only authority
/// distinction the codebase already makes — because inventing a second, finer taxonomy guarantees
/// the two drift apart, and the drift shows up as a route that is gated under one model and open
/// under the other. One taxonomy, expressed twice, with a test asserting the mapping.
/// </summary>
public enum ApiKeyScope
{
    /// <summary>
    /// May call routes that only read. Corresponds to <c>RouteClassification.PublicRead</c>, which is
    /// ungated anyway — so in practice a read-only key buys nothing a bare request does not already
    /// have, and its value is entirely in what it is REFUSED: an admin-mutating route answers 403.
    /// That is the blast-radius reduction #58 exists for.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Full authority: may call every route, including <c>RouteClassification.AdminMutating</c>.
    /// This is what the pre-#58 shared key always was, and what the legacy key continues to be.
    /// </summary>
    Admin,
}
