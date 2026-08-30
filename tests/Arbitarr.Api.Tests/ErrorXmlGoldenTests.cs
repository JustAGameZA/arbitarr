using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Golden-XML tests for error rendering (M1-9): both protocol families render the same
/// &lt;error code="..." description="..."/&gt; element, and RequestLimitReachedException's
/// rate-limit code path never surfaces as a 5xx (that behavior is exercised end-to-end in
/// Arbitarr.Api's SearchEndpoint; here we confirm the rendered XML shape it produces).
/// </summary>
public class ErrorXmlGoldenTests
{
    [Fact]
    public void Torznab_error_renders_code_and_description()
    {
        var xml = TorznabXmlWriter.WriteError(500, "Request limit reached");
        var rendered = xml.ToString();

        Assert.Contains("<error code=\"500\" description=\"Request limit reached\" />", rendered);
    }

    [Fact]
    public void Newznab_error_renders_identical_shape_to_torznab()
    {
        var torznab = TorznabXmlWriter.WriteError(500, "Request limit reached").ToString();
        var newznab = NewznabXmlWriter.WriteError(500, "Request limit reached").ToString();

        Assert.Equal(torznab, newznab);
    }

    [Fact]
    public void Rate_limit_error_uses_SearchEndpoint_documented_code()
    {
        var xml = TorznabXmlWriter.WriteError(Arbitarr.Api.Search.SearchEndpoint.RateLimitErrorCode, "Request limit reached");
        Assert.Contains($"code=\"{Arbitarr.Api.Search.SearchEndpoint.RateLimitErrorCode}\"", xml.ToString());
    }

    [Fact]
    public void Missing_apikey_renders_code_100_in_torznab_wrapper()
    {
        var result = ApiKeyValidator.Validate(providedApiKey: null, expectedApiKey: "correct-key", isTorznab: true);

        Assert.NotNull(result);
        var body = RenderedBody(result!);
        Assert.Contains("<error code=\"100\" description=\"Incorrect user credentials\" />", body);
        Assert.DoesNotContain("newznab", body);
    }

    [Fact]
    public void Wrong_apikey_renders_code_100_in_newznab_wrapper()
    {
        var result = ApiKeyValidator.Validate(providedApiKey: "wrong-key", expectedApiKey: "correct-key", isTorznab: false);

        Assert.NotNull(result);
        var body = RenderedBody(result!);
        Assert.Contains("<error code=\"100\" description=\"Incorrect user credentials\" />", body);
    }

    [Fact]
    public void Correct_apikey_returns_no_error_result()
    {
        var result = ApiKeyValidator.Validate(providedApiKey: "correct-key", expectedApiKey: "correct-key", isTorznab: true);

        Assert.Null(result);
    }

    [Fact]
    public void Empty_configured_apikey_never_authenticates_any_request()
    {
        var result = ApiKeyValidator.Validate(providedApiKey: "anything", expectedApiKey: string.Empty, isTorznab: true);

        Assert.NotNull(result);
    }

    private static string RenderedBody(Microsoft.AspNetCore.Http.IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        using var body = new MemoryStream();
        context.Response.Body = body;

        result.ExecuteAsync(context).GetAwaiter().GetResult();

        body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(body);
        return reader.ReadToEnd();
    }
}
