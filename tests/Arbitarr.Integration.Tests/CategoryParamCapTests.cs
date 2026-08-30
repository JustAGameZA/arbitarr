using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Security-m3 LOW #5: an unbounded <c>cat=</c> query-string list is embedded verbatim in
/// <see cref="Arbitarr.Core.Identity.SearchCacheKeyBuilder"/>'s category component, so
/// <c>Program.cs</c>'s <c>ParseCategories</c> caps it at 64 distinct values before a
/// <see cref="SearchQuery"/> is ever built, independent of what any downstream component does
/// with it.
/// </summary>
public sealed class CategoryParamCapTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ApiKey = "secret-api-key";

    private readonly WebApplicationFactory<Program> _factory;

    public CategoryParamCapTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m3-category-cap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);
        });
    }

    [Fact]
    public async Task A_200_category_query_string_is_capped_at_64_distinct_categories()
    {
        SearchQuery? observed = null;

        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "category-cap-fake-source",
                    onSearch: query => observed = query));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
            });
        });

        using var client = factory.CreateClient();

        var categories = string.Join(',', Enumerable.Range(1, 200));
        var response = await client.GetAsync(
            $"/torznab/api?t=search&q=probe&cat={categories}&apikey={Uri.EscapeDataString(ApiKey)}");
        response.EnsureSuccessStatusCode();

        Assert.NotNull(observed);
        Assert.True(observed!.Categories.Count <= 64, $"Expected at most 64 categories, got {observed.Categories.Count}.");
    }
}
