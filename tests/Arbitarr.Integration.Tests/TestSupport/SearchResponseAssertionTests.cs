using System.Net;
using Xunit;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// arb-wmbp: pins that <see cref="SearchResponseAssertion"/> passes a 200 through silently and
/// fails a non-200 with a message naming both the status code and the body.
///
/// <para><b>Why this helper needs its own tests.</b> It only DOES anything on the failure path:
/// the six callers it replaces would look identical, on the happy path, whether or not it worked
/// at all. An assertion that only ever fires under a CI flake and was never itself proven to fire
/// correctly is exactly the "absence that was never detectable" shape CLAUDE.md §4 warns about.
/// </para>
/// </summary>
public sealed class SearchResponseAssertionTests
{
    [Fact]
    public async Task A_200_response_passes_without_throwing()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        var exception = Record.Exception(() => SearchResponseAssertion.AssertOk(response, body));

        Assert.Null(exception);
    }

    [Fact]
    public void A_500_response_fails_with_the_status_code_and_body_in_the_message()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        const string body = "The indexer encountered an internal error";

        var exception = Record.Exception(() => SearchResponseAssertion.AssertOk(response, body));

        Assert.NotNull(exception);
        Assert.Contains("500", exception.Message, StringComparison.Ordinal);
        Assert.Contains("InternalServerError", exception.Message, StringComparison.Ordinal);
        Assert.Contains(body, exception.Message, StringComparison.Ordinal);
    }
}
