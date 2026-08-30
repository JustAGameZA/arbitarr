using Arbitarr.Api.Rendering;
using Arbitarr.Core.Releases;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// A stable <see cref="RenderedRelease.ProxyGuid"/> is required for the download-proxy path to
/// keep resolving the same release across requests/snapshots. These tests pin that stability
/// and its dependence on both source name and upstream guid.
///
/// Shares the "ReleaseGuidSecret" collection with <c>ReleaseGuidSecretSwapTests</c> so it never
/// runs concurrently with tests that reconfigure <see cref="ReleaseGuid"/>'s shared static secret.
/// </summary>
[Collection("ReleaseGuidSecret")]
public class ReleaseGuidStabilityTests
{
    [Fact]
    public void Same_source_and_guid_always_produce_the_same_proxy_guid()
    {
        var a = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var b = TestReleases.Torrent(sourceName: "eztv", guid: "123", title: "A different title entirely");

        Assert.Equal(a.ProxyGuid, b.ProxyGuid);
    }

    [Fact]
    public void Different_source_names_with_the_same_upstream_guid_produce_different_proxy_guids()
    {
        var a = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var b = TestReleases.Torrent(sourceName: "otherSource", guid: "123");

        Assert.NotEqual(a.ProxyGuid, b.ProxyGuid);
    }

    [Fact]
    public void Different_upstream_guids_with_the_same_source_produce_different_proxy_guids()
    {
        var a = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var b = TestReleases.Torrent(sourceName: "eztv", guid: "456");

        Assert.NotEqual(a.ProxyGuid, b.ProxyGuid);
    }

    [Fact]
    public void Proxy_guid_is_a_lowercase_hex_sha256()
    {
        var release = TestReleases.Torrent();
        Assert.Matches("^[0-9a-f]{64}$", release.ProxyGuid);
    }

    [Fact]
    public void ReleaseGuid_Compute_matches_RenderedRelease_ProxyGuid()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var expected = ReleaseGuid.Compute(new ReleaseIdentity("eztv", "123"));

        Assert.Equal(expected, release.ProxyGuid);
    }
}
