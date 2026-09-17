using System.Net;
using System.Text;
using System.Xml.Linq;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-n5gg: <c>SearchEndpoint</c> registers one ordered array (<c>releasesToRegister</c>) into
/// BOTH the hot <see cref="InMemoryReleaseLookup"/> tier and the persisted
/// <see cref="Arbitarr.Core.Releases.IReleaseLookupStore"/>, but nothing before this test pinned
/// that a REPEATED <see cref="RenderedRelease.ProxyGuid"/> within that one array resolves to the
/// SAME occurrence in both tiers. <see cref="ReleaseGuid.Compute"/> hashes only source name +
/// upstream guid (arb-tps), so one upstream source reporting the same guid twice in a single
/// search response — a real, if unusual, upstream shape — produces exactly this: two
/// <see cref="RenderedRelease"/> entries with an identical <see cref="RenderedRelease.ProxyGuid"/>
/// but distinguishable payloads (different <see cref="ReleaseCandidate.Link"/>, carried through to
/// a distinguishable download body per <see cref="SecondFakeUpstreamSource"/>'s per-instance
/// <c>downloadPayload</c>).
///
/// <para><b>Titles differ so the two candidates do NOT enter one <c>DedupStage</c> group.</b> That
/// scenario (a repeated guid arising from a dedup group's representative and a member) is already
/// covered by <see cref="DedupAlternateMemberDownloadTests"/> (arb-vlsu) and has a completely
/// different resolution path (<c>RenderedRelease.AlternateMembers</c>). Here the repetition is
/// caused by the upstream guid alone, with both candidates surviving the merge as independent,
/// ungrouped top-level entries in <c>filtered</c> — the shape <c>ReleaseLookupStore.UpsertRangeAsync</c>'s
/// arb-c4wh fix (later occurrence in a batch wins) exists for.</para>
/// </summary>
public sealed class RepeatedProxyGuidTierAgreementTests : IAsyncLifetime
{
    private const string ApiKey = "placeholder-repeated-proxy-guid-client-key";
    private const string SourceName = "repeated-guid-source";
    private const string SharedUpstreamGuid = "repeated-guid-upstream-id";

    private const long ReleaseSize = 1_000_000_000;

    private static readonly byte[] FirstPayload = Encoding.UTF8.GetBytes("first-occurrence-payload");
    private static readonly byte[] LaterPayload = Encoding.UTF8.GetBytes("later-occurrence-payload");

    private readonly string _configDirectory;

    public RepeatedProxyGuidTierAgreementTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-repeated-proxy-guid-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        ConfigDirectoryTeardown.Delete(_configDirectory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pairs the owning <see cref="ArbitarrWebApplicationFactory"/> (config-directory access, real
    /// disposal ordering) with the customised <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// <c>WithWebHostBuilder</c> returns — the same two-object shape
    /// <see cref="DedupAlternateMemberDownloadTests"/> uses, since <c>WithWebHostBuilder</c> returns
    /// the base type, not <see cref="ArbitarrWebApplicationFactory"/> itself.
    /// </summary>
    private sealed record Host(ArbitarrWebApplicationFactory Root, WebApplicationFactory<Program> Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Root.DisposeAsync();
    }

    /// <summary>
    /// Builds a fake source that reports the SAME upstream guid twice in one search response, each
    /// occurrence carrying its own distinguishable <see cref="ReleaseCandidate.Link"/>. The LATER
    /// occurrence in the array ("Later Occurrence" / the 192.0.2.72 link) is the one
    /// <c>SearchEndpoint</c>/<c>InMemoryReleaseLookup.RecordAs</c>/
    /// <c>ReleaseLookupStore.UpsertRangeAsync</c> must all agree is the winner.
    /// </summary>
    private static Host BuildFactory(string configDirectory)
    {
        var root = ArbitarrWebApplicationFactory.OverConfigDirectory(configDirectory);
        var client = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<ISourceRegistry>();

                // One IUpstreamSource whose single SearchAsync response contains the SAME
                // Guid twice, under two DIFFERENT titles (so DedupStage's title condition keeps
                // them apart rather than merging them into one dedup group — arb-vlsu's shape is
                // deliberately not what this test exercises) and two different Links, so the
                // resolved payload proves WHICH occurrence a lookup answered with.
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    SourceName,
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Repeated Guid Probe First Occurrence S01E01 1080p WEB-DL",
                            Guid = SharedUpstreamGuid,
                            PubDate = DateTimeOffset.UtcNow,
                            Size = ReleaseSize,
                            Link = new Uri("http://192.0.2.71:8080/getnzb/repeated-guid-first"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Torrent,
                        },
                        new ReleaseCandidate
                        {
                            Title = "Repeated Guid Probe Later Occurrence S01E01 1080p WEB-DL",
                            Guid = SharedUpstreamGuid,
                            PubDate = DateTimeOffset.UtcNow,
                            Size = ReleaseSize,
                            Link = new Uri("http://192.0.2.72:8080/getnzb/repeated-guid-later"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Torrent,
                        },
                    },
                    // FetchDownloadAsync ignores which candidate it was called for and always
                    // returns this single payload, so distinguishing occurrences at the download
                    // proxy has to come from the STORED payload's own metadata below, not from the
                    // source's response. See the assertions: they read the resolved release's own
                    // Link/Title back out of the lookup rather than trusting the download body.
                    downloadPayload: LaterPayload));
                services.AddSingleton<ISourceRegistry>(sp => new StaticSourceRegistry(sp.GetServices<IUpstreamSource>().ToArray()));
            });
        });

        return new Host(root, client);
    }

    /// <summary>
    /// THE POSITIVE CONTROL. Establishes that the two candidates really are distinguishable through
    /// the real search pipeline before either lookup tier is asked to resolve anything — otherwise
    /// "both tiers agree" could pass vacuously because there was only ever one distinct payload.
    /// </summary>
    [Fact]
    public async Task The_two_same_guid_occurrences_are_distinguishable_through_the_search_pipeline()
    {
        await using var host = BuildFactory(_configDirectory);
        using var client = host.Client.CreateClient();

        using var search = await client.GetAsync(
            $"/torznab/api?t=search&q=repeated.guid.probe&apikey={Uri.EscapeDataString(ApiKey)}");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var feed = XDocument.Parse(await search.Content.ReadAsStringAsync());
        var items = feed.Descendants("item").ToList();

        // Two rendered items, not one: proves the two occurrences did NOT collapse into a dedup
        // group (their titles differ), so both survive as independent top-level entries in the
        // rendered response — the shape SearchEndpoint's releasesToRegister array is built from.
        Assert.Equal(2, items.Count);
        var titles = items.Select(i => i.Element("title")?.Value).ToList();
        Assert.Contains(titles, t => t is not null && t.Contains("First Occurrence", StringComparison.Ordinal));
        Assert.Contains(titles, t => t is not null && t.Contains("Later Occurrence", StringComparison.Ordinal));

        // Both items render the SAME guid: proves the repetition this bead is about (one guid, two
        // distinguishable candidates) genuinely reached the render/registration step, rather than
        // only existing in this test's setup.
        var renderedGuid = Assert.Single(items.Select(i => i.Element("guid")?.Value).Distinct());

        // THE POSITIVE CONTROL ITSELF: the lookup this guid resolves to (through the same
        // FindAsync path DownloadProxyEndpoint uses) is the LATER occurrence, not the first — proof
        // that recording both under one key produced a distinguishable, deterministic winner rather
        // than there only ever having been one distinct payload in play.
        var releaseLookup = host.Client.Services.GetRequiredService<InMemoryReleaseLookup>();
        var resolved = await releaseLookup.FindAsync(renderedGuid!);
        Assert.NotNull(resolved);
        Assert.Contains("Later Occurrence", resolved!.Candidate.Title, StringComparison.Ordinal);
        Assert.Equal(new Uri("http://192.0.2.72:8080/getnzb/repeated-guid-later"), resolved.Candidate.Link);
    }

    /// <summary>
    /// THE ASSERTION THIS BEAD EXISTS FOR. One search's <c>releasesToRegister</c> array carries the
    /// same ProxyGuid twice; the hot tier resolves it via a still-warm <see cref="InMemoryReleaseLookup"/>
    /// on the SAME host, and the persisted tier resolves it via a SECOND, fresh host over the SAME
    /// config directory (so its own memory tier starts empty and the read can only be answered by
    /// <c>ReleaseLookupStore.FindAsync</c> — the same "force the persisted path" shape
    /// <c>StatusHealthItemsSurviveRestartTests</c> uses). Both must resolve to the LATER occurrence
    /// ("Later Occurrence" / the 192.0.2.72 link), matching <c>UpsertRangeAsync</c>'s arb-c4wh
    /// comment and <c>InMemoryReleaseLookup.RecordAs</c>'s plain last-write-wins dictionary
    /// assignment.
    /// </summary>
    [Fact]
    public async Task Both_tiers_resolve_a_repeated_guid_to_the_same_later_occurrence()
    {
        string proxyGuid;
        Uri hotTierLink;

        await using (var first = BuildFactory(_configDirectory))
        {
            using var client = first.Client.CreateClient();

            using var search = await client.GetAsync(
                $"/torznab/api?t=search&q=repeated.guid.probe&apikey={Uri.EscapeDataString(ApiKey)}");
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);

            var feed = XDocument.Parse(await search.Content.ReadAsStringAsync());
            var items = feed.Descendants("item").ToList();
            Assert.Equal(2, items.Count);

            // Both rendered items carry the SAME guid — the repetition this bead is about — so the
            // ProxyGuid used to query both tiers below is read from the rendered response itself,
            // not recomputed.
            var renderedGuids = items.Select(i => i.Element("guid")?.Value).Distinct().ToList();
            proxyGuid = Assert.Single(renderedGuids)!;

            // Read the hot tier back THROUGH ITS OWN RESOLUTION PATH (FindAsync) — the same lookup
            // DownloadProxyEndpoint performs. RecordRange recorded both occurrences under this one
            // key in order, so only the dictionary's last write survives; FindAsync is what proves
            // which occurrence that was.
            var releaseLookup = first.Client.Services.GetRequiredService<InMemoryReleaseLookup>();
            var hotResolved = await releaseLookup.FindAsync(proxyGuid);
            Assert.NotNull(hotResolved);
            hotTierLink = hotResolved!.Candidate.Link;

            // Give the durable write (arb-tps: awaited on the search path) a chance to land before
            // this host goes away — SearchEndpoint already awaits UpsertRangeAsync, so by the time
            // the search response above was received the write was complete; no extra wait needed.
        }

        // A genuinely separate host, same config directory (same SQLite file), so its
        // InMemoryReleaseLookup starts EMPTY and can only answer from ReleaseLookupStore.FindAsync
        // (the durable tier) — exactly the "force the persisted path" shape
        // StatusHealthItemsSurviveRestartTests uses for the same reason.
        await using var second = BuildFactory(_configDirectory);
        using var secondClient = second.Client.CreateClient();

        var secondReleaseLookup = second.Client.Services.GetRequiredService<InMemoryReleaseLookup>();
        Assert.Null(await secondReleaseLookup.FindAsync(proxyGuid));

        var persistentLookup = second.Client.Services.GetRequiredService<IReleaseLookup>();
        var persistedResolved = await persistentLookup.FindAsync(proxyGuid);

        Assert.NotNull(persistedResolved);

        // THE PIN: the persisted tier's answer carries the SAME link the hot tier answered with —
        // both resolve to the later occurrence, never the first. A tier that disagreed here would
        // mean a client retrying a download across a restart (or after the hot tier's 30-minute
        // TTL) could be handed a DIFFERENT release than the one the original search response
        // pointed at.
        Assert.Equal(hotTierLink, persistedResolved!.Candidate.Link);
        Assert.Equal(new Uri("http://192.0.2.72:8080/getnzb/repeated-guid-later"), persistedResolved.Candidate.Link);
        Assert.Contains("Later Occurrence", persistedResolved.Candidate.Title, StringComparison.Ordinal);
    }
}
