using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-x7w8.15: <see cref="DownloadProxyEndpoint"/> asserted PER PROTOCOL KIND.
///
/// <para>The three arms are asserted separately and can fail independently, because "a download
/// succeeded" is exactly the shape CLAUDE.md §4 calls out: a Usenet release proxies; a
/// <c>.torrent</c> at an ordinary http(s) URL proxies through the IDENTICAL bounded path; a magnet
/// is answered by redirect and is NEVER fetched. The never-fetched assertion is made directly on
/// <see cref="FakeUpstreamSource.DownloadRequests"/> rather than inferred from the status code — a
/// 302 alone does not prove the adapter was skipped, and an implementation that fetched first and
/// redirected afterwards would return the same 302.</para>
/// </summary>
public class DownloadProxyProtocolArmTests
{
    private const string ValidApiKey = "secret-api-key";
    private const string InfoHash = "332afa1fd16fc0a5fd8d54e18d62e57f60a06764";
    private const string MagnetLink = $"magnet:?xt=urn:btih:{InfoHash}&dn=Some.Release.1080p&tr=udp%3A%2F%2Ftracker.example%3A1337";

    private static IClientApiKeyResolver Resolver() => new SingleKeyResolver(ValidApiKey);

    private static RenderedRelease MagnetRelease(string sourceName = "eztv") =>
        new(sourceName, new ReleaseCandidate
        {
            Title = "Some.Release.1080p",
            Guid = "magnet-1",
            PubDate = TestReleases.FixedPubDate,
            Size = 1138166333,
            Link = new Uri(MagnetLink),
            Category = new[] { 5000 },
            Protocol = ProtocolKind.Torrent,
            InfoHash = InfoHash,
        });

    /// <summary>
    /// A release whose declared protocol is <see cref="ProtocolKind.Unknown"/> but whose link is a
    /// magnet. The link is the stronger signal and decides alone.
    /// </summary>
    private static RenderedRelease UnknownProtocolMagnetRelease(string sourceName = "eztv") =>
        new(sourceName, new ReleaseCandidate
        {
            Title = "Some.Release.1080p",
            Guid = "magnet-unknown",
            PubDate = TestReleases.FixedPubDate,
            Size = 1138166333,
            Link = new Uri(MagnetLink),
            Protocol = ProtocolKind.Unknown,
        });

    // ---- The Usenet arm -----------------------------------------------------------------------

    /// <summary>
    /// A Usenet release proxies: bytes, and the octet-stream default. Asserted on the result TYPE
    /// (the house pattern in <c>DownloadProxyTests</c>) rather than a status code.
    /// </summary>
    [Fact]
    public async Task A_usenet_release_proxies_bytes_as_octet_stream()
    {
        var release = TestReleases.Usenet(sourceName: "usenetsrc", guid: "abc123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var payload = "nzb-bytes"u8.ToArray();
        var source = new FakeUpstreamSource("usenetsrc", downloadFactory: () => new MemoryStream(payload));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), NullEventSink.Instance, CancellationToken.None);

        var bytes = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal(payload, bytes.FileContents);
        Assert.Equal("application/octet-stream", bytes.ContentType);

        // It went through the adapter exactly once, for THIS candidate.
        Assert.Equal(release.Candidate.Guid, Assert.Single(source.DownloadRequests).Guid);
    }

    // ---- The .torrent arm ---------------------------------------------------------------------

    /// <summary>
    /// A torrent whose link is an ordinary http(s) <c>.torrent</c> URL proxies IDENTICALLY to an
    /// NZB: the same adapter call, the same bounded buffering, the same payload. Asserted on the
    /// path taken (the adapter was asked, for this candidate) rather than merely on a 200 — the
    /// bead's requirement is that no second fetch path exists.
    /// </summary>
    [Fact]
    public async Task A_torrent_file_release_proxies_through_the_identical_path_as_an_nzb()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var payload = "torrent-bytes"u8.ToArray();
        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream(payload));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), NullEventSink.Instance, CancellationToken.None);

        var bytes = Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal(payload, bytes.FileContents);
        await RedirectResponseAssertions.AssertIsNotARedirectAsync(result);

        // The identical path: the adapter WAS asked, for this candidate, exactly once — the same
        // assertion the Usenet arm above makes.
        Assert.Equal(release.Candidate.Guid, Assert.Single(source.DownloadRequests).Guid);
    }

    /// <summary>
    /// arb-ywcj, folded in here: the content type is derived AT THE ROUTE from
    /// <see cref="ReleaseCandidate.Protocol"/>, which the endpoint already holds — rather than by
    /// widening <c>IUpstreamSource.FetchDownloadAsync</c> into a result type. Asserted for the two
    /// kinds SEPARATELY so a single hardcoded string cannot satisfy both.
    /// </summary>
    [Fact]
    public async Task A_torrent_payload_is_served_as_x_bittorrent_and_an_nzb_as_octet_stream()
    {
        var torrent = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var usenet = TestReleases.Usenet(sourceName: "usenetsrc", guid: "abc123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(torrent);
        lookup.Record(usenet);

        var sources = new IUpstreamSource[]
        {
            new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("torrent-bytes"u8.ToArray())),
            new FakeUpstreamSource("usenetsrc", downloadFactory: () => new MemoryStream("nzb-bytes"u8.ToArray())),
        };
        var registry = new StaticSourceRegistry(sources);

        var torrentResult = await DownloadProxyEndpoint.HandleAsync(
            torrent.ProxyGuid, ValidApiKey, Resolver(), lookup, registry, NullEventSink.Instance, CancellationToken.None);
        var usenetResult = await DownloadProxyEndpoint.HandleAsync(
            usenet.ProxyGuid, ValidApiKey, Resolver(), lookup, registry, NullEventSink.Instance, CancellationToken.None);

        Assert.Equal("application/x-bittorrent", Assert.IsType<FileContentHttpResult>(torrentResult).ContentType);
        Assert.Equal("application/octet-stream", Assert.IsType<FileContentHttpResult>(usenetResult).ContentType);
    }

    // ---- The magnet arm -----------------------------------------------------------------------

    /// <summary>
    /// The core assertion: a magnet is answered with a redirect carrying the magnet in Location, and
    /// the source is NEVER asked for it.
    ///
    /// <para>The never-fetched half is asserted DIRECTLY on the spy, not inferred from the status
    /// code. An implementation that fetched the payload first and redirected afterwards returns the
    /// same 302 and would pass a status-only test while sending the indexer's key upstream for a
    /// release that needs no key at all.</para>
    /// </summary>
    [Fact]
    public async Task A_magnet_release_redirects_and_the_source_is_never_fetched()
    {
        var release = MagnetRelease();
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("must-not-be-read"u8.ToArray()));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), NullEventSink.Instance, CancellationToken.None);

        // Asserted on the RESPONSE (302 + Location) rather than on the result's type, per
        // RedirectResponseAssertions — the endpoint no longer returns the framework's redirect
        // result, and this is the stronger statement anyway.
        await RedirectResponseAssertions.AssertRedirectsToAsync(result, MagnetLink);

        // "A magnet is never fetched as HTTP" — the assertion the bead names, made on the spy.
        Assert.Empty(source.DownloadRequests);

        // And it is a redirect, not bytes: the mirrored type assertion of the proxy arms above.
        Assert.IsNotType<FileContentHttpResult>(result);
    }

    /// <summary>
    /// Mutation guard for the assertion above, mirroring
    /// <c>DownloadProxyTests.Proxy_mode_serves_bytes_and_never_a_redirect_result</c> in the opposite
    /// direction: a proxying release returns <c>FileContentHttpResult</c> and DOES reach the adapter.
    /// Together the two prove the magnet test can fail — it is not passing because every release
    /// redirects, nor because the fake is never called on any path.
    /// </summary>
    [Fact]
    public async Task The_never_fetched_assertion_bites_because_a_non_magnet_release_does_fetch()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("torrent-bytes"u8.ToArray()));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), NullEventSink.Instance, CancellationToken.None);

        // Same fake, same route, same key — and here it IS asked. So Assert.Empty above is a claim
        // about the magnet arm, not a property of the fixture.
        Assert.NotEmpty(source.DownloadRequests);
        Assert.IsType<FileContentHttpResult>(result);
    }

    /// <summary>
    /// The link decides, not the declared protocol. <c>TorznabFeedParser</c> never produces
    /// <see cref="ProtocolKind.Unknown"/>, but the route handles it defensively: a magnet link on an
    /// Unknown-protocol release still redirects and still fetches nothing. Branching on the declared
    /// protocol instead would send this down the proxy path and attempt an HTTP request against a
    /// <c>magnet:</c> URI.
    /// </summary>
    [Fact]
    public async Task A_magnet_link_on_an_unknown_protocol_release_still_redirects_without_fetching()
    {
        var release = UnknownProtocolMagnetRelease();
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("must-not-be-read"u8.ToArray()));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), NullEventSink.Instance, CancellationToken.None);

        await RedirectResponseAssertions.AssertRedirectsToAsync(result, MagnetLink);
        Assert.Empty(source.DownloadRequests);
    }

    /// <summary>
    /// The magnet arm sits AFTER source resolution, deliberately: a release attributed to a source
    /// the operator has since disabled or removed answers 404, exactly as an NZB does. The 404 is
    /// about what Arbitarr is CONFIGURED to serve, not about whether a credential is needed — so "a
    /// magnet needs no key" is not a reason to let it bypass the gate.
    /// </summary>
    [Fact]
    public async Task A_magnet_from_a_since_disabled_source_still_answers_404_and_never_redirects()
    {
        var release = MagnetRelease(sourceName: "removed-indexer");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        // The source that produced it is no longer configured; another one is.
        var other = new FakeUpstreamSource("still-configured", downloadFactory: () => new MemoryStream("bytes"u8.ToArray()));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { other }), NullEventSink.Instance, CancellationToken.None);

        Assert.IsType<NotFound>(result);
        await RedirectResponseAssertions.AssertIsNotARedirectAsync(result);
        Assert.Empty(other.DownloadRequests);
    }

    /// <summary>
    /// SEC-L1: the magnet arm sits after the apikey check too, so it cannot turn the route into an
    /// OPEN REDIRECTOR. An unauthenticated caller gets the bare 401 and no Location header.
    /// </summary>
    [Fact]
    public async Task A_magnet_is_not_reachable_without_a_valid_client_key()
    {
        var release = MagnetRelease();
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("bytes"u8.ToArray()));
        var registry = new StaticSourceRegistry(new IUpstreamSource[] { source });

        var missing = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, apikey: null, Resolver(), lookup, registry, NullEventSink.Instance, CancellationToken.None);
        var wrong = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, "wrong-key", Resolver(), lookup, registry, NullEventSink.Instance, CancellationToken.None);

        foreach (var result in new[] { missing, wrong })
        {
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

            // "No Location header" asserted on the RESPONSE, which is what an open-redirector claim
            // is actually about. The type check this replaces could not see a Location header set by
            // any other result type, so it was the weaker form of this assertion.
            await RedirectResponseAssertions.AssertIsNotARedirectAsync(result);
        }
    }

    // ---- Bookkeeping: the two arms answer differently ------------------------------------------

    /// <summary>
    /// A magnet redirect records NO successful grab: nothing was fetched from the indexer, so ADR
    /// 0020's grab allowance is not consumed and the sticky refusal health item must NOT clear.
    ///
    /// <para>Asserted against a tracker that is PRE-SEEDED with a refusal, so the assertion is about
    /// a state that would visibly change. An empty tracker staying empty proves nothing — it is the
    /// vacuous shape CLAUDE.md §4 names.</para>
    /// </summary>
    [Fact]
    public async Task A_magnet_redirect_records_no_successful_grab_so_a_sticky_refusal_survives()
    {
        var release = MagnetRelease();
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var tracker = new DownloadRefusalTracker();
        await tracker.RecordRefusalAsync("eztv", "Refused HTTP 302: the source redirected instead of serving the file.", DateTimeOffset.UnixEpoch);

        // The positive control: the item IS there to be cleared before the request runs.
        Assert.Equal("eztv", Assert.Single(tracker.Snapshot()).SourceName);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("must-not-be-read"u8.ToArray()));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), NullEventSink.Instance, CancellationToken.None,
            refusalTracker: tracker);

        await RedirectResponseAssertions.AssertRedirectsToAsync(result, MagnetLink);
        Assert.Empty(source.DownloadRequests);

        // Still refused: Arbitarr observed no payload, so nothing demonstrated the download path works.
        Assert.Equal("eztv", Assert.Single(tracker.Snapshot()).SourceName);
    }

    /// <summary>
    /// The mirrored direction, and what makes the assertion above bite: a <c>.torrent</c> PROXY from
    /// the same source, through the same route, with the same pre-seeded tracker, DOES clear the
    /// item — because a payload actually came back. Two different answers on one route, which is
    /// exactly what a single "a download succeeded" test would paper over.
    /// </summary>
    [Fact]
    public async Task A_torrent_proxy_does_clear_the_sticky_refusal_so_the_magnet_assertion_bites()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var tracker = new DownloadRefusalTracker();
        await tracker.RecordRefusalAsync("eztv", "Refused HTTP 302: the source redirected instead of serving the file.", DateTimeOffset.UnixEpoch);
        Assert.Equal("eztv", Assert.Single(tracker.Snapshot()).SourceName);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("torrent-bytes"u8.ToArray()));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), NullEventSink.Instance, CancellationToken.None,
            refusalTracker: tracker);

        Assert.IsType<FileContentHttpResult>(result);
        Assert.NotEmpty(source.DownloadRequests);
        Assert.Empty(tracker.Snapshot());
    }

    /// <summary>
    /// The magnet URI is upstream-supplied text, so it goes in exactly ONE place: the Location
    /// header. It must reach no event summary, reason or detail — the rule
    /// <see cref="DownloadProxyEndpoint"/>'s two existing comments state for a Location header and a
    /// refusal reason.
    ///
    /// <para>Non-vacuous per CLAUDE.md §4: the info hash is first shown to be present in what the
    /// route was GIVEN, then asserted absent from every recorded event field, PER EVENT — so this
    /// cannot pass merely because the needle was never in play.</para>
    /// </summary>
    [Fact]
    public async Task The_magnet_reaches_the_location_header_and_no_event_field()
    {
        var release = MagnetRelease();
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        // The needle IS in the input the route was handed...
        Assert.Contains(InfoHash, release.Candidate.Link.OriginalString, StringComparison.Ordinal);

        var events = new RecordingEventSink();
        var tracker = new DownloadRefusalTracker();
        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("must-not-be-read"u8.ToArray()));

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup,
            new StaticSourceRegistry(new IUpstreamSource[] { source }), events, CancellationToken.None,
            refusalTracker: tracker);

        // ...and it IS in the Location header, which is the one permitted destination. Read off the
        // executed response rather than the result object, so this proves the header was actually
        // written (see RedirectResponseAssertions).
        var redirected = new DefaultHttpContext();
        await result.ExecuteAsync(redirected);
        Assert.Equal(StatusCodes.Status302Found, redirected.Response.StatusCode);
        Assert.Contains(InfoHash, redirected.Response.Headers.Location.ToString(), StringComparison.Ordinal);

        // Per row, over every field an event can carry. A magnet redirect writes no event at all,
        // and that is asserted too — but the per-field sweep is what would catch a future arm that
        // starts recording one.
        foreach (var recorded in events.Events)
        {
            Assert.DoesNotContain(InfoHash, recorded.Summary ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(InfoHash, recorded.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(InfoHash, recorded.SourceDisplayName ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(InfoHash, recorded.Detail ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.Empty(events.Events);

        // And no health item either: per row over the reason text, for the same rule.
        foreach (var refusal in tracker.Snapshot())
        {
            Assert.DoesNotContain(InfoHash, refusal.Reason, StringComparison.Ordinal);
        }

        Assert.Empty(tracker.Snapshot());
    }

    /// <summary>Captures every event the endpoint records, so the sweep above can read each field.</summary>
    private sealed class RecordingEventSink : IEventSink
    {
        public List<RecordedEvent> Events { get; } = new();

        public ValueTask RecordAsync(
            RecordedEventKind kind,
            string summary,
            string? reason = null,
            string? sourceDisplayName = null,
            string? detail = null,
            CancellationToken cancellationToken = default,
            bool? shadowMode = null)
        {
            Events.Add(new RecordedEvent(kind, summary, reason, sourceDisplayName, detail, shadowMode));
            return ValueTask.CompletedTask;
        }
    }
}
