using Arbitarr.Api.Search;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Exercises <see cref="DownloadProxyEndpoint"/>: resolves a known proxy guid back to its
/// upstream source and streams the payload; unknown guids 404; a source's
/// <see cref="RequestLimitReachedException"/> surfaces as 429, never a 5xx; a missing/incorrect
/// apikey is rejected with a bare 401 before any lookup/streaming happens (SEC-L1 amendment).
/// </summary>
public class DownloadProxyTests
{
    private const string ValidApiKey = "secret-api-key";

    private static IClientApiKeyResolver Resolver() => new SingleKeyResolver(ValidApiKey);

    [Fact]
    public async Task Known_proxy_guid_streams_the_upstream_payload()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var payload = "torrent-bytes"u8.ToArray();
        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream(payload));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, CancellationToken.None);

        Assert.IsAssignableFrom<IResult>(result);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Unknown_proxy_guid_returns_not_found()
    {
        var lookup = new InMemoryReleaseLookup();
        var sources = Array.Empty<IUpstreamSource>();

        var result = await DownloadProxyEndpoint.HandleAsync("does-not-exist", ValidApiKey, Resolver(), lookup, sources, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Source_not_found_for_recorded_release_returns_not_found()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        // No sources registered for "eztv" at all.
        var sources = Array.Empty<IUpstreamSource>();

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task RequestLimitReachedException_surfaces_as_429_not_5xx()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadException: new RequestLimitReachedException("eztv"));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status429TooManyRequests, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task Missing_apikey_returns_bare_401_before_lookup()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, apikey: null, Resolver(), lookup, sources, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task Wrong_apikey_returns_bare_401_before_lookup()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, "wrong-key", Resolver(), lookup, sources, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized, statusCodeResult.StatusCode);
    }
}
