using System.Net;
using System.Xml.Linq;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-tps: THE TWO TESTS THE BEAD SAYS NOBODY HAD. Both drive a real search and a real
/// <c>/download/{proxyGuid}</c> through the composed host, and both reproduce a condition that
/// answered <b>404</b> before the durable lookup existed:
///
/// <list type="number">
/// <item>the process restarted between the search and the grab — the in-memory lookup is
/// process-lifetime, so every link died on every restart (29 starts in 25 hours on the reporting
/// instance);</item>
/// <item>more than 30 minutes passed between the search and the grab — the in-memory tier's
/// <see cref="InMemoryReleaseLookup.EntryTtl"/> — which *arr delay profiles routinely exceed.</item>
/// </list>
///
/// <para>These are host-level rather than unit tests on purpose, for the same reason
/// <c>MintedClientApiKeyTests</c> is: the defect was never that a store was wrong, it was that the
/// composed ROUTE resolved through a lookup that forgot. Only driving the real routes through the
/// real container can establish that it no longer does.</para>
/// </summary>
public sealed class ReleaseLookupSurvivesRestartAndTtlTests : IAsyncLifetime
{
    // "secret-api-key" is the prefix the pre-commit secret guard allowlists.
    private const string ClientKey = "secret-api-key-release-lookup-tests";
    private const string SourceName = "fake-hydra";
    private const string UpstreamGuid = "upstream-guid-arb-tps";

    /// <summary>The bytes the fake upstream serves, so a successful download is identifiable rather than merely non-404.</summary>
    private static readonly byte[] PayloadBytes = "NZB-PAYLOAD-ARB-TPS"u8.ToArray();

    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(), "arbitarr-release-lookup-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebApplicationFactory<Program>> _factories = new();

    /// <summary>
    /// Builds a host over the SHARED config directory, so a second one opens the same
    /// <c>arbitarr.db</c> — which is what makes "restart" a real restart rather than a fresh
    /// install. Each host gets its own in-memory lookup, exactly as a restarted process does.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);
            builder.UseSetting("Arbitarr:ApiKey", ClientKey);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(
                    new IUpstreamSource[] { new PayloadServingSource(SourceName, Candidate(), PayloadBytes) });
            });
        });

        _factories.Add(factory);
        return factory;
    }

    private static ReleaseCandidate Candidate() => new()
    {
        Title = "Some.Release.S01E01.1080p.WEB-DL",
        Guid = UpstreamGuid,
        PubDate = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero),
        Size = 1_234_567_890,
        Link = new Uri("https://indexer.example.invalid/getnzb/arb-tps"),
        Category = new[] { 5040 },
        Protocol = ProtocolKind.Usenet,
    };

    public ReleaseLookupSurvivesRestartAndTtlTests() => Directory.CreateDirectory(_configDirectory);

    /// <summary>
    /// THE RESTART FIX. Searches on one host, disposes it entirely, then brings up a SECOND host
    /// over the same database — which is a restart in every respect that matters here: a brand new
    /// <see cref="InMemoryReleaseLookup"/>, and nothing carried over but the file on disk. The link
    /// the first host issued must still serve its payload.
    ///
    /// <para>The assertion is on the BODY, not merely the status. A 404 and a 200-with-nothing are
    /// distinguishable outcomes, and only serving the actual bytes proves the candidate survived
    /// the round trip intact enough for the source to fetch with — which is the whole point, since
    /// the source re-validates the link's origin at fetch time.</para>
    /// </summary>
    [Fact]
    public async Task A_link_issued_before_a_restart_still_downloads_after_it()
    {
        string proxyGuid;

        var first = CreateHost();
        using (var client = first.CreateClient())
        {
            proxyGuid = await SearchAndExtractProxyGuidAsync(client);

            // Establish the link worked BEFORE the restart, so a success afterwards is attributable
            // to the durable lookup rather than to the link having been resolvable all along by
            // some other means — and a failure afterwards to the restart rather than to a bad guid.
            await AssertDownloadServesPayloadAsync(client, proxyGuid);
        }

        await first.DisposeAsync();
        _factories.Remove(first);

        var restarted = CreateHost();
        using (var client = restarted.CreateClient())
        {
            // The new host's memory tier genuinely does not know this guid — without this the test
            // would pass just as happily if the two hosts had somehow shared one lookup, which
            // would make it evidence for nothing.
            var memory = restarted.Services.GetRequiredService<InMemoryReleaseLookup>();
            Assert.Null(await memory.FindAsync(proxyGuid));

            await AssertDownloadServesPayloadAsync(client, proxyGuid);
        }
    }

    /// <summary>
    /// THE TTL FIX. Clears the in-memory tier rather than moving a clock, which is the honest way
    /// to express this condition: <see cref="InMemoryReleaseLookup.EntryTtl"/> is a
    /// <see langword="static readonly"/> constant read against the injected
    /// <see cref="TimeProvider"/>, and the composed host resolves the real one — so there is no
    /// clock to advance without also replacing the host's TimeProvider, which would change what is
    /// under test. An entry evicted by the TTL and an entry that was never there are the same state
    /// as far as every reader is concerned: a memory miss. That miss is what this drives.
    ///
    /// <para>The eviction is done through the type's own bound rather than by reaching into it: the
    /// lookup is capped at <see cref="InMemoryReleaseLookup.MaxEntries"/>, so recording that many
    /// unrelated releases evicts the one under test by the class's own documented policy.</para>
    /// </summary>
    [Fact]
    public async Task A_link_whose_memory_entry_is_gone_still_downloads_from_the_store()
    {
        var host = CreateHost();
        using var client = host.CreateClient();

        var proxyGuid = await SearchAndExtractProxyGuidAsync(client);
        await AssertDownloadServesPayloadAsync(client, proxyGuid);

        var memory = host.Services.GetRequiredService<InMemoryReleaseLookup>();
        Assert.NotNull(await memory.FindAsync(proxyGuid));

        EvictEverythingFromMemory(memory);

        // The precondition this test exists for: memory has forgotten, exactly as it would 30
        // minutes after the search. Before arb-tps this state answered 404.
        Assert.Null(await memory.FindAsync(proxyGuid));

        await AssertDownloadServesPayloadAsync(client, proxyGuid);

        // And the store hit repopulated the fast path, so the next grab of the same release does
        // not pay the database round trip again.
        Assert.NotNull(await memory.FindAsync(proxyGuid));
    }

    /// <summary>
    /// The negative control for both tests above: a guid that was never issued is still a 404.
    /// Without this, a lookup that resolved ANY guid to something fetchable would pass both tests
    /// and have broken the enumeration property the proxy guid exists to provide.
    /// </summary>
    [Fact]
    public async Task A_guid_that_was_never_issued_is_still_not_found()
    {
        var host = CreateHost();
        using var client = host.CreateClient();
        _ = await SearchAndExtractProxyGuidAsync(client);

        using var response = await client.GetAsync(
            $"/download/never-issued-guid?apikey={Uri.EscapeDataString(ClientKey)}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Fills the in-memory lookup past <see cref="InMemoryReleaseLookup.MaxEntries"/> so its own
    /// oldest-first eviction discards everything recorded before this call.
    /// </summary>
    private static void EvictEverythingFromMemory(InMemoryReleaseLookup memory)
    {
        for (var i = 0; i <= InMemoryReleaseLookup.MaxEntries; i++)
        {
            memory.Record(new RenderedRelease(SourceName, FillerCandidate(i)));
        }
    }

    private static ReleaseCandidate FillerCandidate(int i) => new()
    {
        Title = $"Filler.Release.{i}",
        Guid = $"filler-{i}",
        PubDate = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero),
        Size = 1,
        Link = new Uri($"https://indexer.example.invalid/getnzb/filler-{i}"),
        Protocol = ProtocolKind.Usenet,
    };

    /// <summary>
    /// Drives a real search and pulls the proxy guid out of the rendered enclosure URL — the same
    /// link an *arr app would follow, rather than a guid computed by the test. A guid the test
    /// derived itself would not prove the link the response actually carried is resolvable.
    /// </summary>
    private static async Task<string> SearchAndExtractProxyGuidAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            $"/newznab/api?t=search&q=some+release&apikey={Uri.EscapeDataString(ClientKey)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("error code", body, StringComparison.OrdinalIgnoreCase);

        var item = Assert.Single(XDocument.Parse(body).Descendants("item"));
        var enclosureUrl = item.Elements("enclosure").Single().Attribute("url")!.Value;

        var path = new Uri(enclosureUrl).AbsolutePath;
        Assert.StartsWith("/download/", path, StringComparison.Ordinal);
        return Uri.UnescapeDataString(path["/download/".Length..]);
    }

    private static async Task AssertDownloadServesPayloadAsync(HttpClient client, string proxyGuid)
    {
        using var response = await client.GetAsync(
            $"/download/{Uri.EscapeDataString(proxyGuid)}?apikey={Uri.EscapeDataString(ClientKey)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PayloadBytes, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// A fake upstream that returns one release and serves real bytes for it, so a download can be
    /// asserted by its CONTENT. <c>SecondFakeUpstreamSource</c> serves an empty stream, which cannot
    /// distinguish "fetched the right release" from "fetched nothing".
    /// </summary>
    private sealed class PayloadServingSource : IUpstreamSource
    {
        private readonly ReleaseCandidate _release;
        private readonly byte[] _payload;

        public PayloadServingSource(string name, ReleaseCandidate release, byte[] payload)
        {
            Name = name;
            _release = release;
            _payload = payload;
        }

        public string Name { get; }

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReleaseCandidate>>(new[] { _release });

        public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceCaps(
                SupportedCategories: new[] { 5040 },
                SupportsTvSearch: true,
                SupportsMovieSearch: true,
                MaxPageSize: null));

        /// <summary>
        /// Serves the payload only for the release this source actually knows, by upstream guid. A
        /// source that served bytes for anything would let a lookup returning the WRONG release
        /// still pass every download assertion in this file.
        /// </summary>
        public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_release.Guid, release.Guid);
            Assert.Equal(_release.Link, release.Link);
            return Task.FromResult<Stream>(new MemoryStream(_payload));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        // The delete used to run bare inside an empty catch (IOException): no pool clear, so it lost
        // to a pooled handle and said nothing (arb-gphi). ConfigDirectoryTeardown does both halves
        // and throws if the delete still fails.
        ConfigDirectoryTeardown.Delete(_configDirectory);
    }
}
