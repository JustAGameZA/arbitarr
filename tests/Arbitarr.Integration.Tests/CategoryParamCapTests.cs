using Arbitarr.Core.Sources;
using Arbitarr.Integration.Tests.TestSupport;
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
public sealed class CategoryParamCapTests : IAsyncLifetime
{
    private const string ApiKey = "secret-api-key";

    private readonly ArbitarrWebApplicationFactory _root;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configDirectory;

    public CategoryParamCapTests()
    {
        _configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m3-category-cap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);

        _root = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
        _factory = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// This class OWNS its host, and that ownership is the load-bearing part (arb-gphi fix-up).
    /// It previously injected the shared <c>IClassFixture&lt;WebApplicationFactory&lt;Program&gt;&gt;</c>
    /// and derived from it, which meant the running host belonged to the shared root factory and
    /// lived until the assembly finished — so deleting the config directory from this class's
    /// <c>Dispose</c> removed files a live host still held open, and
    /// <see cref="ConfigDirectoryTeardown.Delete"/> threw by design, failing whichever test was
    /// disposing. That is the arb-rwhb hazard: THE HOST MUST BE FULLY STOPPED BEFORE THE CONFIG
    /// DIRECTORY IS DELETED.
    ///
    /// <para><see cref="IAsyncLifetime"/>, never bare <c>System.IAsyncDisposable</c>: xunit v2 awaits
    /// the former and silently ignores the latter (measured — see the interface note on
    /// <c>MintedClientApiKeyTests</c>). Awaiting the factory's own <c>DisposeAsync</c> FIRST is what
    /// drains the host's background work and returns its pooled handles; only then can the delete
    /// win. <see cref="ConfigDirectoryTeardown"/> carries why the pool clear and the delete are both
    /// required.</para>
    ///
    /// <para><see cref="ArbitarrWebApplicationFactory.OverConfigDirectory"/> deliberately does NOT
    /// delete a caller-supplied directory on disposal, so there is no double delete here — it clears
    /// the pools and leaves the directory to its owner, which is this class.</para>
    /// </summary>
    public async Task DisposeAsync()
    {
        await _root.DisposeAsync();
        ConfigDirectoryTeardown.Delete(_configDirectory);
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
                services.RemoveAll<ISourceRegistry>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "category-cap-fake-source",
                    onSearch: query => observed = query));
                services.AddSingleton<ISourceRegistry>(sp => new StaticSourceRegistry(sp.GetServices<IUpstreamSource>().ToArray()));
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
