using Arbitarr.Core.Sources;
using Arbitarr.Integration.Tests.TestSupport;
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
public sealed class IdParamClampEndpointTests : IAsyncLifetime
{
    private const string ApiKey = "secret-api-key";

    private readonly ArbitarrWebApplicationFactory _root;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configDirectory;

    public IdParamClampEndpointTests()
    {
        _configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m3-idclamp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);

        _root = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
        _factory = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// This class OWNS its host so disposal drains it before the delete — see
    /// <see cref="CategoryParamCapTests.DisposeAsync"/> for the full account of why the shared
    /// <c>IClassFixture</c> this class used to inject made the delete throw (arb-gphi fix-up).
    /// </summary>
    public async Task DisposeAsync()
    {
        await _root.DisposeAsync();
        ConfigDirectoryTeardown.Delete(_configDirectory);
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

    /// <summary>
    /// #104: an EMPTY <c>tvdbid=</c> must be treated as absent, not rejected. Binding it as
    /// <c>int?</c> made minimal-API fail the request before the endpoint ran, answering an indexer
    /// route — contractually XML — with a 400 and a <c>text/plain</c>
    /// <c>BadHttpRequestException</c> body ("Failed to bind parameter"), and the search never ran
    /// at all even though <c>q</c>/<c>season</c>/<c>ep</c> alone could serve it.
    ///
    /// <para>
    /// The assertions are on the <see cref="SearchQuery"/> that reached the source, not merely on
    /// the status code: a 200 alone would also be produced by a route that swallowed the empty id
    /// AND the rest of the query. Observing that <c>q</c>/<c>season</c>/<c>ep</c> arrived intact is
    /// what proves the request was actually served rather than merely not-refused.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Empty_tvdbid_is_treated_as_absent_and_the_search_still_runs()
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
            $"/newznab/api?t=tvsearch&q=probe&tvdbid=&season=22&ep=1&apikey={Uri.EscapeDataString(ApiKey)}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/rss+xml", response.Content.Headers.ContentType?.MediaType);

        Assert.NotNull(observed);
        Assert.Null(observed!.TvdbId);
        Assert.Equal("probe", observed.QueryText);
        Assert.Equal(22, observed.Season);
        Assert.Equal(1, observed.Episode);
        Assert.Equal(SearchType.TvSearch, observed.Type);
    }

    /// <summary>
    /// The same for a non-numeric id, which hit the identical binding failure. It is treated as
    /// absent rather than 400 for the same reason: the query's other identity signals still serve it.
    /// </summary>
    [Fact]
    public async Task Non_numeric_tvdbid_is_treated_as_absent_and_the_search_still_runs()
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
            $"/torznab/api?t=search&q=probe&tvdbid=not-a-number&apikey={Uri.EscapeDataString(ApiKey)}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(observed);
        Assert.Null(observed!.TvdbId);
        Assert.Equal("probe", observed.QueryText);
    }

    /// <summary>
    /// #104's headline case end to end on the real route: <c>t=tvsearch</c> with <c>q</c> and
    /// numbering but NO id must reach the source as a TV search that still carries its season and
    /// episode, so the source forwards <c>t=tvsearch&amp;q=…&amp;season=22&amp;ep=1</c> upstream.
    /// The <c>t=search</c> half of the theory is the control: the identical request differing only
    /// in its inbound mode must NOT become a TV search, which is what proves the mode is read from
    /// the request rather than inferred from the presence of season/ep.
    /// </summary>
    [Theory]
    [InlineData("tvsearch", SearchType.TvSearch)]
    [InlineData("movie", SearchType.Movie)]
    [InlineData("search", SearchType.Search)]
    public async Task Inbound_search_type_reaches_the_upstream_query(string inboundType, SearchType expected)
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
            $"/newznab/api?t={inboundType}&q=Project+Runway&season=22&ep=1&apikey={Uri.EscapeDataString(ApiKey)}");
        response.EnsureSuccessStatusCode();

        Assert.NotNull(observed);
        Assert.Equal(expected, observed!.Type);
        Assert.Equal(22, observed.Season);
        Assert.Equal(1, observed.Episode);
        Assert.Null(observed.TvdbId);
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
