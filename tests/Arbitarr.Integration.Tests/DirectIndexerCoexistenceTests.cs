using System.Net;
using System.Xml.Linq;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-x7w8.17 (migration-proof half): proves that admitting a directly-configured Newznab/Torznab
/// indexer is <b>strictly additive</b> to an existing NZBHydra2 deployment, over the REAL host
/// composition — not a claim about <c>DedupStage</c> or <c>UpstreamMergeStage</c> in isolation,
/// both of which already have their own unit coverage. This is CLAUDE.md §4's "no behaviour change
/// of its own" made a fact: every fixture below drives <c>/torznab/api</c> exactly as Sonarr/Radarr
/// would, so a regression here is a regression a real client would see.
///
/// <para>Three fixtures, one per bead-named scenario, each with its own <see cref="ArbitarrWebApplicationFactory"/>
/// and its own purpose-built <see cref="SecondFakeUpstreamSource"/> instances — deliberately not
/// extending <see cref="SecondFakeUpstreamSource"/> itself or sharing a registry with another
/// in-flight file, per the collision note on arb-x7w8.17's brief (three branches were touching that
/// fake concurrently; a fourth touching it is the one thing to avoid).</para>
///
/// <para>"Hydra" vs. "direct indexer" below is naming only: both legs are backed by the same
/// <see cref="SecondFakeUpstreamSource"/> type, one instance per role. Which concrete adapter
/// (<c>NzbHydraSource</c> vs. <c>NewznabSource</c>) gets selected for a given source kind is
/// covered elsewhere (ADR 0022 / arb-x7w8.2), not by this fixture.</para>
/// </summary>
public sealed class DirectIndexerCoexistenceTests
{
    // RFC 5737 TEST-NET-1: non-routable, and no real address is committed.
    private const string HydraOnlyLinkHost = "192.0.2.100";
    private const string HydraLinkHost = "192.0.2.101";
    private const string DirectLinkHost = "192.0.2.102";

    private const string ApiKey = "placeholder-coexistence-client-key";

    private static ReleaseCandidate MakeRelease(
        string guid,
        string title,
        string linkHost,
        long size = 1_500_000_000,
        ProtocolKind protocol = ProtocolKind.Usenet) => new()
    {
        Title = title,
        Guid = guid,
        PubDate = DateTimeOffset.UtcNow,
        Size = size,
        Link = new Uri($"http://{linkHost}:8080/getnzb/{guid}"),
        Category = new[] { 5000 },
        Protocol = protocol,
    };

    private static async Task<(HttpStatusCode Status, List<XElement> Items)> SearchAsync(
        HttpClient client, string query)
    {
        using var response = await client.GetAsync(
            $"/torznab/api?t=search&q={Uri.EscapeDataString(query)}&apikey={Uri.EscapeDataString(ApiKey)}");
        var body = await response.Content.ReadAsStringAsync();
        var items = response.StatusCode == HttpStatusCode.OK
            ? XDocument.Parse(body).Descendants("item").ToList()
            : new List<XElement>();
        return (response.StatusCode, items);
    }

    /// <summary>
    /// Builds a real host whose only configured <see cref="IUpstreamSource"/>s are exactly
    /// <paramref name="sources"/>, so the search path exercised is the actual merge/dedup pipeline
    /// (<c>UpstreamMergeStage</c> -&gt; <c>DedupStage</c> -&gt; rendering), not a hand-built subject.
    /// </summary>
    private static (ArbitarrWebApplicationFactory Root, WebApplicationFactory<Program> Factory, string ConfigDirectory)
        BuildHost(string configDirectoryTag, params IUpstreamSource[] sources)
    {
        var configDirectory = Path.Combine(
            Path.GetTempPath(), $"arbitarr-{configDirectoryTag}", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);

        var root = ArbitarrWebApplicationFactory.OverConfigDirectory(configDirectory);
        var factory = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<ISourceRegistry>();
                foreach (var source in sources)
                {
                    services.AddSingleton(source);
                }

                services.AddSingleton<ISourceRegistry>(
                    sp => new StaticSourceRegistry(sp.GetServices<IUpstreamSource>().ToArray()));
            });
        });

        return (root, factory, configDirectory);
    }

    /// <summary>
    /// SCENARIO 1 (bead): a Hydra-only deployment is unchanged. Asserts BOTH halves the bead names —
    /// the resolved source set (one source, and it is the Hydra one) AND the emitted request shape
    /// (the exact <see cref="SearchQuery"/> the adapter received), because asserting only the set
    /// proves nothing about what actually goes out on the wire. A regression that silently mutated
    /// the query on its way to the sole configured source — dropping the category filter, say —
    /// would pass a set-only assertion and fail this one.
    /// </summary>
    [Fact]
    public async Task A_hydra_only_deployment_resolves_one_source_and_emits_the_query_unmodified()
    {
        SearchQuery? observedQuery = null;

        var hydraOnly = new SecondFakeUpstreamSource(
            "coexistence-hydra-only",
            searchResults: new[] { MakeRelease("hydra-only-1", "Coexistence Hydra Only Probe", HydraOnlyLinkHost) },
            onSearch: q => observedQuery = q);

        var (root, factory, configDirectory) = BuildHost("coexistence-hydra-only-tests", hydraOnly);
        try
        {
            using var client = factory.CreateClient();

            var (status, items) = await SearchAsync(client, "coexistence hydra only probe");

            Assert.Equal(HttpStatusCode.OK, status);
            var item = Assert.Single(items);
            Assert.Equal("Coexistence Hydra Only Probe", item.Element("title")?.Value);

            // The request shape actually reached the (only) adapter — not merely that a 200 came
            // back, which a broken merge could still produce from an empty set under some renderer
            // defaults.
            Assert.NotNull(observedQuery);
            Assert.Equal("coexistence hydra only probe", observedQuery!.QueryText);
            Assert.Equal(SearchProtocol.Torznab, observedQuery.Protocol);
        }
        finally
        {
            await root.DisposeAsync();
            ConfigDirectoryTeardown.Delete(configDirectory);
        }
    }

    /// <summary>
    /// SCENARIO 2 (bead): Hydra plus one direct indexer both contribute to ONE merge, asserted PER
    /// SOURCE. Two DISTINCT, NAMED releases — one only Hydra could have produced, one only the direct
    /// indexer could have produced — so an implementation that returned two results from a single
    /// source (or dropped one source's contribution while still returning a set of size 2 some other
    /// way) cannot pass. This is the bead's own non-vacuity requirement and CLAUDE.md §4's per-row
    /// rule.
    /// </summary>
    [Fact]
    public async Task Hydra_and_a_direct_indexer_both_contribute_a_named_result_to_one_merge()
    {
        var hydra = new SecondFakeUpstreamSource(
            "coexistence-hydra",
            searchResults: new[] { MakeRelease("hydra-merge-1", "Coexistence Merge Probe From Hydra", HydraLinkHost) });
        var direct = new SecondFakeUpstreamSource(
            "coexistence-direct-indexer",
            searchResults: new[] { MakeRelease("direct-merge-1", "Coexistence Merge Probe From Direct Indexer", DirectLinkHost) });

        var (root, factory, configDirectory) = BuildHost("coexistence-merge-tests", hydra, direct);
        try
        {
            using var client = factory.CreateClient();

            var (status, items) = await SearchAsync(client, "coexistence merge probe");

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(2, items.Count);

            var titles = items.Select(i => i.Element("title")?.Value).ToArray();
            Assert.Contains("Coexistence Merge Probe From Hydra", titles);
            Assert.Contains("Coexistence Merge Probe From Direct Indexer", titles);

            // Two DISTINCT rendered guids, not one item repeated: rules out a renderer bug that
            // returned "2 items" while actually emitting the same source's release twice.
            var guids = items.Select(i => i.Element("guid")?.Value).ToArray();
            Assert.Equal(2, guids.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            await root.DisposeAsync();
            ConfigDirectoryTeardown.Delete(configDirectory);
        }
    }

    /// <summary>
    /// SCENARIO 3 (bead): the same release arriving from both Hydra and a direct indexer collapses
    /// to one result once dedup is in — and, the non-vacuity half the bead calls out explicitly, two
    /// GENUINELY DIFFERENT releases from the two sources must NOT collapse. Both assertions live in
    /// one fixture and one search so an implementation that merged everything unconditionally (which
    /// would pass a collapse-only test) fails the second half, and one that never merged anything
    /// (which would pass a non-collapse-only test) fails the first.
    /// </summary>
    [Fact]
    public async Task The_same_release_from_both_sources_collapses_while_a_different_release_does_not()
    {
        const string sharedTitle = "Coexistence Dedup Probe S01E01 1080p WEB-DL";
        const long sharedSize = 2_000_000_000;

        var hydra = new SecondFakeUpstreamSource(
            "coexistence-dedup-hydra",
            searchResults: new[]
            {
                MakeRelease("dedup-hydra-shared", sharedTitle, HydraLinkHost, sharedSize, ProtocolKind.Torrent),
                MakeRelease("dedup-hydra-unique", "Coexistence Dedup Probe Only Hydra Has", HydraLinkHost, protocol: ProtocolKind.Torrent),
            });
        var direct = new SecondFakeUpstreamSource(
            "coexistence-dedup-direct",
            searchResults: new[]
            {
                // Same normalised title, size within DedupStage's tolerance, same known protocol —
                // ADR 0019's three conditions — so this collapses with the Hydra copy above.
                MakeRelease("dedup-direct-shared", sharedTitle, DirectLinkHost, sharedSize, ProtocolKind.Torrent),
                // A genuinely different release: different title entirely. Must NOT collapse with
                // anything, proving the merge is conservative rather than merging everything from a
                // two-source deployment unconditionally.
                MakeRelease("dedup-direct-unique", "Coexistence Dedup Probe Only Direct Has", DirectLinkHost, protocol: ProtocolKind.Torrent),
            });

        var (root, factory, configDirectory) = BuildHost("coexistence-dedup-tests", hydra, direct);
        try
        {
            using var client = factory.CreateClient();

            var (status, items) = await SearchAsync(client, "coexistence dedup probe");

            Assert.Equal(HttpStatusCode.OK, status);

            // Three rendered items: the shared release collapsed from two copies into one, and the
            // two unique releases (one per source) each remain their own item. Four raw candidates
            // in, three items out is the collapse; anything else (2 or 4) means either an
            // over-merge or no merge at all happened.
            Assert.Equal(3, items.Count);

            var titles = items.Select(i => i.Element("title")?.Value).ToArray();
            Assert.Single(titles, t => t == sharedTitle);
            Assert.Contains("Coexistence Dedup Probe Only Hydra Has", titles);
            Assert.Contains("Coexistence Dedup Probe Only Direct Has", titles);
        }
        finally
        {
            await root.DisposeAsync();
            ConfigDirectoryTeardown.Delete(configDirectory);
        }
    }
}
