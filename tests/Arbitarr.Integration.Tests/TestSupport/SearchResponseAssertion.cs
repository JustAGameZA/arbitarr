using System.Net;
using Xunit;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// Asserts that a search response came back <see cref="HttpStatusCode.OK"/>, naming the actual
/// status code and the response body when it did not (arb-krtr, arb-wmbp).
///
/// <para><b>Why this is ONE helper rather than the six-times-copied
/// <c>Assert.Equal(HttpStatusCode.OK, response.StatusCode)</c> it replaces.</b> A bare
/// <c>Assert.Equal</c> here said only "Expected OK, Actual X" on failure: nothing about WHAT the
/// server actually sent back, which is exactly what made a CI flake in
/// <c>RedirectAccessModeKeyNeverReachesLogsTests</c> undiagnosable (arb-krtr, #547). Fixing that
/// once per copy would have left five siblings with the same gap the next time one flaked; a
/// shared helper means there is one message to get right, not seven.</para>
///
/// <para><b>Printing the body is safe only because of what the caller is asserting on.</b> Every
/// caller here drives <c>/newznab/api?t=search</c> (or <c>/torznab/api?t=search</c>), and that
/// route's only two non-OK outcomes (<c>SearchEndpoint.InfrastructureErrorResult</c> and
/// <c>NoSourceAnsweredResult</c>) both render a FIXED literal description
/// (<c>SearchEndpoint.InfrastructureErrorDescription</c>) and never the underlying exception or
/// any upstream text (CLAUDE.md §1), so a non-OK body from this route cannot carry the planted
/// indexer key, the client key, or any other secret under test. This helper must not be pointed
/// at a route whose error body is not similarly fixed, without re-establishing that guarantee for
/// the new caller.</para>
/// </summary>
internal static class SearchResponseAssertion
{
    /// <summary>
    /// Asserts <paramref name="response"/> is <see cref="HttpStatusCode.OK"/>. On failure, the
    /// assertion message names the actual status code and <paramref name="body"/> so a CI failure
    /// here is diagnosable without a repro.
    /// </summary>
    /// <param name="response">The response under assertion.</param>
    /// <param name="body">
    /// The response body, already read by the caller (a caller that needs the body afterwards,
    /// to parse the XML say, must not have this helper consume the content stream a second
    /// time).
    /// </param>
    public static void AssertOk(HttpResponseMessage response, string body)
    {
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Search returned {(int)response.StatusCode} {response.StatusCode}, body: {body}");
    }
}
