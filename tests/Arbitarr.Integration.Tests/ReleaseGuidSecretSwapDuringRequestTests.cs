using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
/// arb-0hd0: THE TEST THAT WOULD HAVE CAUGHT arb-agh. A link issued by a running host must still
/// download after the process-global proxy-guid secret changes underneath it — which is what a
/// second host starting in the same process does.
///
/// <para><b>Why this is a host test and not a unit test.</b> The defect was never in one method.
/// <c>RenderedRelease.ProxyGuid</c> was a computed property re-evaluated five times per search, and
/// the failure needed two of those evaluations — the one that produced the LOOKUP KEY and the one
/// that produced the URL HANDED TO THE CLIENT — to disagree. Only a real search through the
/// composed host puts both on the same object with the store and the memory tier behind them, so
/// only here can "the link the response carried resolves" be asserted rather than assumed.</para>
///
/// <para><b>The swap is a real swap, not a stand-in for one.</b> <see cref="ReleaseGuid.Configure"/>
/// is public and is called here directly, so this exercises the static regardless of how a host
/// happens to obtain its secret. That independence is deliberate: <c>ArbitarrWebApplicationFactory</c>
/// now supplies <c>Arbitarr:ReleaseGuidSecret</c> per builder so routine host builds stop rewriting
/// the global, and a test that relied on a host build to move the secret would quietly stop
/// testing anything the day that setting was extended to every factory. This one cannot.</para>
///
/// <para><b>Collection.</b> This assembly runs its classes in parallel (see AssemblyInfo.cs), and
/// this class writes a process-global. It therefore has the assembly's own serialised collection to
/// itself — xunit collections do not cross assembly boundaries, so Arbitarr.Api.Tests'
/// <c>ReleaseGuidSecret</c> collection does not cover anything here. Nothing else may join it.</para>
/// </summary>
[Collection(ReleaseGuidSecretSwapDuringRequestTests.CollectionName)]
public sealed class ReleaseGuidSecretSwapDuringRequestTests : IAsyncLifetime
{
    internal const string CollectionName = "ReleaseGuidSecret(Integration)";

    // "secret-api-key" is the prefix the pre-commit secret guard allowlists.
    private const string ClientKey = "secret-api-key-guid-secret-swap-tests";
    private const string SourceName = "fake-hydra";
    private const string UpstreamGuid = "upstream-guid-arb-0hd0";

    /// <summary>The bytes the fake upstream serves, so a successful download is identifiable rather than merely non-404.</summary>
    private static readonly byte[] PayloadBytes = "NZB-PAYLOAD-ARB-0HD0"u8.ToArray();

    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(), "arbitarr-guid-secret-swap-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebApplicationFactory<Program>> _factories = new();

    /// <summary>Set once by <see cref="SecretSwappingStore"/> so the swap happens on one upsert only.</summary>
    private readonly StrongBox<int> _swapped = new(0);

    public ReleaseGuidSecretSwapDuringRequestTests() => Directory.CreateDirectory(_configDirectory);

    /// <summary>
    /// Changes the secret DURING the search — between the evaluation that produces the lookup key
    /// and the evaluation that produces the URL handed to the client — then grabs the link the
    /// response actually carried. Before the fix that download was a 404 with no exception and no
    /// log line, because those two evaluations had been keyed by different secrets.
    ///
    /// <para><b>Why the swap is mid-request and not between the search and the grab</b>, which is
    /// the obvious way to write this and is VACUOUS. <c>SearchEndpoint</c> records the release in
    /// both lookup tiers and renders its URL within ONE request; a swap after the response has been
    /// written finds every tier already keyed consistently, and the later download is a plain
    /// dictionary hit on a key nobody recomputes. That version of this test PASSES against the
    /// unfixed computed-property shape — measured while writing it, not assumed — so it would have
    /// been evidence for nothing. The defect needs the secret to move BETWEEN two evaluations of
    /// one release's guid, which is exactly what a computed property permits and a materialised one
    /// forbids.</para>
    ///
    /// <para><b>The seam</b> is <see cref="IReleaseLookupStore.UpsertRangeAsync"/>, which
    /// <c>SearchEndpoint</c> calls after computing the persisted key and before rendering the URL.
    /// A decorator that flips the secret when it is called reproduces the race deterministically —
    /// no sleeps, no threads, no timing. In production the flip comes from a second host's
    /// <c>Program.cs</c>; the two hosts were 0.5 ms apart in the arb-agh trace.</para>
    ///
    /// <para>The POSITIVE CONTROL is what stops the assertion from being vacuous: it establishes
    /// that the swap actually moved the secret, so a success afterwards is attributable to the
    /// materialised guid rather than to the swap having been a no-op. Without it this test would
    /// pass just as happily against a <see cref="ReleaseGuid.Configure"/> that did nothing.
    /// <c>Arbitarr.Api.Tests.ReleaseGuidSecretSwapTests.Configure_WithDifferentSecret_ProducesDifferentGuidForSameInput</c>
    /// already proves the mechanism in isolation; this re-establishes it for the identity this test
    /// actually uses, rather than restating that test's body.</para>
    /// </summary>
    [Fact]
    public async Task A_secret_change_during_the_search_does_not_break_the_link_it_issued()
    {
        ReleaseGuid.Configure(RandomNumberGenerator.GetBytes(32));

        try
        {
            var identity = new ReleaseIdentity(SourceName, UpstreamGuid);
            var guidBeforeSwap = ReleaseGuid.Compute(identity);

            var host = CreateHost();
            using var client = host.CreateClient();

            // The search itself performs the swap, at the seam described above.
            var proxyGuid = await SearchAndExtractProxyGuidAsync(client);

            // POSITIVE CONTROL: the swap really happened and really changed the guid for this
            // identity. If this failed, the assertion below would prove nothing — so it is
            // asserted rather than assumed.
            Assert.NotEqual(guidBeforeSwap, ReleaseGuid.Compute(identity));

            // THE ASSERTION. The link the response carried still resolves and still serves the
            // right bytes, because every evaluation of this release's guid within the request was
            // one materialised value rather than several re-derivations straddling the swap.
            await AssertDownloadServesPayloadAsync(client, proxyGuid);
        }
        finally
        {
            // A FRESH RANDOM secret: restoring a known fixed value would leak it into every other
            // test in this process. Same reasoning, and the same shape, as
            // Arbitarr.Api.Tests.ReleaseGuidSecretSwapTests.
            ReleaseGuid.Configure(RandomNumberGenerator.GetBytes(32));
        }
    }

    // NOTE ON THE SECOND TEST asked for alongside this one, for MintedClientApiKeyTests' path: not
    // added, because it would share this test's code rather than exercise a second path. That path
    // reaches /download/{proxyGuid} through the same DownloadProxyEndpoint, the same two lookup
    // tiers and the same RenderedRelease; only the client key's provenance differs, and the proxy
    // guid does not depend on it. A second copy would add a test that cannot fail unless this one
    // does, which is worse than no test: it reads as independent evidence and is not.

    /// <summary>
    /// Builds a host over this class's own config directory with one fake upstream, and decorates
    /// the release-lookup store so the secret swap happens mid-search.
    ///
    /// <para>Deliberately a plain <see cref="WebApplicationFactory{TEntryPoint}"/> rather than
    /// <c>ArbitarrWebApplicationFactory</c>: the only secret movement in this test must be the one
    /// the decorator performs, so the assertion is about that window and nothing else.</para>
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

                // Decorate the real store so the secret changes mid-request, at the one point that
                // sits between the persisted-key evaluation and the URL evaluation. The real store
                // is kept underneath rather than replaced, so the durable tier still behaves
                // normally and the test is not quietly reduced to a memory-tier test.
                // Program.cs registers this with a factory, so the inner instance is obtained by
                // invoking that same factory rather than by activating an implementation type
                // (there is none to activate).
                var storeDescriptor = services.Single(d => d.ServiceType == typeof(IReleaseLookupStore));
                var innerFactory = storeDescriptor.ImplementationFactory!;
                services.Remove(storeDescriptor);
                services.AddScoped<IReleaseLookupStore>(
                    sp => new SecretSwappingStore((IReleaseLookupStore)innerFactory(sp), _swapped));
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
        Link = new Uri("https://indexer.example.invalid/getnzb/arb-0hd0"),
        Category = new[] { 5040 },
        Protocol = ProtocolKind.Usenet,
    };

    /// <summary>
    /// Drives a real search and pulls the proxy guid out of the rendered enclosure URL — the same
    /// link an *arr app would follow. A guid the test computed itself would be the very thing under
    /// test, and would pass whether or not the response carried a resolvable one.
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

    /// <summary>
    /// Asserts on the BODY, not merely the status: a 404 and a 200-with-nothing are distinguishable
    /// outcomes, and only the actual bytes prove the lookup resolved to the right release.
    /// </summary>
    private static async Task AssertDownloadServesPayloadAsync(HttpClient client, string proxyGuid)
    {
        using var response = await client.GetAsync(
            $"/download/{Uri.EscapeDataString(proxyGuid)}?apikey={Uri.EscapeDataString(ClientKey)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PayloadBytes, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// Wraps the real <see cref="IReleaseLookupStore"/> and changes <see cref="ReleaseGuid"/>'s
    /// process-global secret once the first upsert has been applied — after both lookup tiers hold
    /// the release and before the XML (and its download URL) is rendered, which happens in the
    /// caller after <c>SearchEndpoint</c> returns. This is the arb-agh window, made deterministic:
    /// in production a second host's startup lands here by chance.
    ///
    /// <para>Only once, so the download request later in the test does not keep moving the secret
    /// underneath the assertion — the test is about one search's internal consistency, not about
    /// churn. The inner store is still called, so the durable tier behaves exactly as it normally
    /// would and this does not silently degrade into a memory-tier-only test.</para>
    /// </summary>
    private sealed class SecretSwappingStore : IReleaseLookupStore
    {
        private readonly StrongBox<int> _swapped;
        private readonly IReleaseLookupStore _inner;

        public SecretSwappingStore(IReleaseLookupStore inner, StrongBox<int> swapped)
        {
            _inner = inner;
            _swapped = swapped;
        }

        public async Task UpsertRangeAsync(IEnumerable<StoredRelease> releases, CancellationToken cancellationToken = default)
        {
            // AFTER the inner upsert, and that ordering is the whole test. Swapping BEFORE it makes
            // this test vacuous: the caller has already computed the StoredRelease keys, but the
            // rows land under the PRE-swap key while the URL is rendered POST-swap, so the durable
            // tier still matches the link and PersistentReleaseLookup serves the download from its
            // store fallback — measured, and it passes against the unfixed shape. Swapping after
            // leaves both tiers keyed pre-swap and only the URL post-swap, which is the actual
            // arb-agh state: a link matching NEITHER tier.
            await _inner.UpsertRangeAsync(releases, cancellationToken).ConfigureAwait(false);

            // The store is registered SCOPED, so a new instance exists per request; the flag is
            // shared by the test instance rather than by the class, so it is not static state that
            // would make a second run in the same process skip the swap entirely.
            if (Interlocked.Exchange(ref _swapped.Value, 1) == 0)
            {
                ReleaseGuid.Configure(RandomNumberGenerator.GetBytes(32));
            }
        }

        public Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default) =>
            _inner.FindAsync(proxyGuid, cancellationToken);
    }

    /// <summary>
    /// A fake upstream that returns one release and serves real bytes for it, so a download can be
    /// asserted by its CONTENT rather than by a status code alone.
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
        /// Serves the payload only for the release this source actually knows, by upstream guid, so
        /// a lookup that resolved to the WRONG release cannot pass the download assertions.
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

/// <summary>
/// Serialises <see cref="ReleaseGuidSecretSwapDuringRequestTests"/> against the rest of this
/// assembly. That class writes <c>ReleaseGuid</c>'s process-global secret, so it must not run
/// beside a class holding a live host — the whole point of arb-0hd0 is what happens when it does.
///
/// <para>arb-tl8l: <c>DisableParallelization</c> on a collection definition stops the WHOLE assembly
/// — every parallel slot, not merely the other tests in this file — and that breadth is the point,
/// not an oversight to be tightened later. The secret this class swaps is process-global, so any
/// live host anywhere in the assembly reads the swapped value for the duration. Narrowing this to an
/// intra-class collection (or to <c>[Collection]</c> on this class alone) would leave the other
/// slots running against a secret they did not set, which is precisely the failure being tested for
/// rather than a condition to reproduce accidentally. The cost is that this class blocks the
/// assembly while it runs; that cost is accepted.</para>
/// </summary>
[CollectionDefinition(ReleaseGuidSecretSwapDuringRequestTests.CollectionName, DisableParallelization = true)]
public class ReleaseGuidSecretIntegrationCollection
{
}
