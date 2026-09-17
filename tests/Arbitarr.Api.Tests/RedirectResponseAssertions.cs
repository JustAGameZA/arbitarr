using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Asserts on the 302 an <see cref="IResult"/> actually WRITES, rather than on which concrete result
/// type it happens to be.
///
/// <para><b>Why these tests stopped type-checking the result (arb-j4hq).</b> They asserted
/// <c>Assert.IsType&lt;RedirectHttpResult&gt;(result)</c> and read <c>.Url</c> off it. That pinned an
/// IMPLEMENTATION CHOICE — "this endpoint calls <c>Results.Redirect</c>" — while saying nothing
/// about the response a client receives. The distinction turned out to matter: the framework's
/// redirect result logs its whole destination, which for the redirect access mode is the indexer's
/// URL with its API key in it, so the endpoint now writes the 302 itself. The response is unchanged;
/// only the type is. Tests that assert the OBSERVABLE response keep their full meaning across that
/// change and would keep it across the next one.</para>
///
/// <para>This is strictly stronger than the type check it replaces, not a weakening to accommodate
/// the new type. <c>IsType</c> plus <c>.Url</c> never proved the status code was 302, nor that the
/// header was written at all — the result object merely carried a string. Executing the result and
/// reading the response proves both, which is what the callers were relying on it to mean.</para>
/// </summary>
internal static class RedirectResponseAssertions
{
    /// <summary>
    /// Executes <paramref name="result"/> against a fresh <see cref="DefaultHttpContext"/> and
    /// asserts it produced a 302 whose <c>Location</c> is exactly <paramref name="expectedLocation"/>.
    ///
    /// <para>Compared with <see cref="StringComparison.Ordinal"/> against the raw header value: the
    /// caller is a download client that hands the URL back to the indexer, so an escaping change
    /// inside the query would be a real difference and must not be normalised away by round-tripping
    /// through <see cref="Uri"/>.</para>
    /// </summary>
    internal static async Task AssertRedirectsToAsync(IResult result, string expectedLocation)
    {
        var context = NewContext();

        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(expectedLocation, context.Response.Headers.Location.ToString());
    }

    /// <summary>
    /// Executes <paramref name="result"/> and asserts it is NOT a redirect: no 3xx status and no
    /// <c>Location</c> header.
    ///
    /// <para>Replaces <c>Assert.IsNotType&lt;RedirectHttpResult&gt;</c>, and is what that assertion
    /// was always trying to say. Both halves are checked because either alone is satisfiable by an
    /// implementation nobody wants: a 302 with no Location, or a 200 that sets one.</para>
    /// </summary>
    internal static async Task AssertIsNotARedirectAsync(IResult result)
    {
        var context = NewContext();

        await result.ExecuteAsync(context);

        Assert.False(
            context.Response.StatusCode is >= 300 and <= 399,
            $"Expected a non-redirect response, but the status code was {context.Response.StatusCode}.");
        Assert.True(
            string.IsNullOrEmpty(context.Response.Headers.Location.ToString()),
            "Expected no Location header on a non-redirect response.");
    }

    /// <summary>
    /// A context an arbitrary <see cref="IResult"/> can execute against.
    ///
    /// <para>The service provider is not optional furniture: several framework results resolve
    /// <see cref="ILoggerFactory"/> from <c>RequestServices</c> when they execute, and a bare
    /// <see cref="DefaultHttpContext"/> has none, so they throw before writing anything. A null
    /// logger factory keeps the execution real while writing nothing anywhere — which also means
    /// these helpers never themselves become a place a destination URL could be logged.</para>
    /// </summary>
    private static DefaultHttpContext NewContext() => new()
    {
        RequestServices = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(new LoggerFactory())
            .BuildServiceProvider(),
    };
}
