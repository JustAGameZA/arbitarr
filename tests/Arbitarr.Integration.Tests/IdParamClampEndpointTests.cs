using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Security-m3 MEDIUM #4: <c>Program.cs</c> clamps <c>tvdbid</c>/<c>tmdbid</c>/<c>season</c>/
/// <c>ep</c> via <see cref="Arbitarr.Api.Search.IdParamClamp"/> before building a
/// <see cref="SearchQuery"/>, so an out-of-range value never widens the cache key space -- it is
/// dropped to null (falling back to the query's other identity signals) instead of being kept
/// verbatim. A valid, in-range id is preserved unchanged.
/// </summary>
public sealed class IdParamClampEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ApiKey = "secret-api-key";

    private readonly WebApplicationFactory<Program> _factory;

    public IdParamClampEndpointTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m3-idclamp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);
        });
    }

    [Fact]
    public async Task Out_of_range_tvdbid_falls_back_to_null_before_reaching_the_upstream_query()
    {
        SearchQuery? observed = null;

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "idclamp-fake-source",
                    onSearch: query => observed = query));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
            });
        });

        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/torznab/api?t=search&q=probe&tvdbid=-1&apikey={Uri.EscapeDataString(ApiKey)}");
        response.EnsureSuccessStatusCode();

        Assert.NotNull(observed);
        Assert.Null(observed!.TvdbId);
    }

    [Fact]
    public async Task Valid_tvdbid_is_preserved_unchanged()
    {
        SearchQuery? observed = null;

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "idclamp-fake-source",
                    onSearch: query => observed = query));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
            });
        });

        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/torznab/api?t=search&q=probe&tvdbid=74796&apikey={Uri.EscapeDataString(ApiKey)}");
        response.EnsureSuccessStatusCode();

        Assert.NotNull(observed);
        Assert.Equal(74796, observed!.TvdbId);
    }
}
