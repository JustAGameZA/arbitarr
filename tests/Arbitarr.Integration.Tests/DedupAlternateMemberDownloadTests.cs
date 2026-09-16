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
/// arb-vlsu: SearchEndpoint used to register only each dedup group's REPRESENTATIVE in the release
/// lookup, so a group member's <see cref="RenderedRelease.ProxyGuid"/> had no entry in either tier —
/// <c>DownloadProxyEndpoint</c> 404s on a lookup miss, so a client handed a group could never grab
/// the fallback <c>RenderedRelease.AlternateMembers</c> exists to offer. This drives the REAL host
/// composition (CLAUDE.md §4: "the endpoint that bypassed the dispatcher" is exactly the shape a
/// hand-built lookup would miss) with two enabled fake sources whose releases collapse to one dedup
/// group, then GETs <c>/download/{proxyGuid}</c> for the ALTERNATE member's own guid and asserts the
/// fetch reaches the member's OWN configured source — not the representative's — since a lookup bug
/// that resolved every guid to the representative would otherwise pass a bare "not 404" check.
///
/// <para>Exercises BOTH lookup tiers in one pass: <c>Program.cs</c> always wires
/// <c>PersistentReleaseLookup</c> (memory first, the SQLite-backed <c>IReleaseLookupStore</c> behind
/// it) over this factory's real config directory, so a request that only the durable tier could have
/// answered still proves the fix — there is no separate memory-only composition to fall back to.</para>
///
/// <para>The alternate member's proxy guid is never exposed on any HTTP response (neither the
/// Torznab/Newznab XML nor the JSON ad-hoc search surface renders <c>AlternateMembers</c>), so the
/// test reads it back from the DI-resolved <see cref="InMemoryReleaseLookup"/> singleton's
/// <see cref="InMemoryReleaseLookup.Snapshot"/> immediately after the search — the SAME registration
/// this bead's fix performs, not a recomputation. Recomputing via <see cref="ReleaseGuid.Compute"/>
/// directly was tried and is deliberately NOT used: that HMAC secret is a process-global static
/// (arb-0hd0/arb-agh), and this assembly runs its test classes in parallel, so another class's host
/// startup calling <see cref="ReleaseGuid.Configure"/> between this search and the recomputation
/// races the secret out from under it — measured as a flaky failure in the full suite, not assumed.
/// Reading the value the lookup already holds has no such window.</para>
/// </summary>
public sealed class DedupAlternateMemberDownloadTests : IAsyncLifetime
{
    private const string ApiKey = "placeholder-dedup-alternate-member-client-key";

    private const string RepresentativeSourceName = "dedup-alt-source-a";
    private const string MemberSourceName = "dedup-alt-source-b";

    private const string RepresentativeUpstreamGuid = "dedup-alt-a-guid";
    private const string MemberUpstreamGuid = "dedup-alt-b-guid";

    private const long ReleaseSize = 1_000_000_000;

    private readonly ArbitarrWebApplicationFactory _root;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configDirectory;

    public DedupAlternateMemberDownloadTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-dedup-alternate-member-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);

        _root = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
        _factory = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                // Two independent fake sources, each returning one release with the same normalised
                // title, an equal size and the same known protocol — DedupStage's three conditions —
                // so they collapse into a single dedup group with a non-empty AlternateMembers. Source
                // name (alphabetical: "dedup-alt-source-a" < "-b") together with AllEqualSourcePriority
                // (Program.cs's default) makes the "a" source the deterministic representative.
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<ISourceRegistry>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    RepresentativeSourceName,
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Dedup Alternate Member Probe S01E01 1080p WEB-DL",
                            Guid = RepresentativeUpstreamGuid,
                            PubDate = DateTimeOffset.UtcNow,
                            Size = ReleaseSize,
                            Link = new Uri("http://192.0.2.61:8080/getnzb/dedup-alt-a"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Torrent,
                        },
                    },
                    downloadPayload: Encoding.UTF8.GetBytes(RepresentativeSourceName)));
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    MemberSourceName,
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Dedup Alternate Member Probe S01E01 1080p WEB-DL",
                            Guid = MemberUpstreamGuid,
                            PubDate = DateTimeOffset.UtcNow,
                            Size = ReleaseSize,
                            Link = new Uri("http://192.0.2.62:8080/getnzb/dedup-alt-b"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Torrent,
                        },
                    },
                    downloadPayload: Encoding.UTF8.GetBytes(MemberSourceName)));
                services.AddSingleton<ISourceRegistry>(sp => new StaticSourceRegistry(sp.GetServices<IUpstreamSource>().ToArray()));
            });
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
    public async Task Downloading_an_alternate_members_guid_is_served_by_the_members_own_source()
    {
        using var client = _factory.CreateClient();

        // Runs the real search: DedupStage collapses the two sources' releases into one group, and
        // (pre-fix) SearchEndpoint registered only the representative's ProxyGuid in the lookup.
        using var search = await client.GetAsync(
            $"/torznab/api?t=search&q=dedup.alternate.member.probe&apikey={Uri.EscapeDataString(ApiKey)}");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var feed = XDocument.Parse(await search.Content.ReadAsStringAsync());
        var items = feed.Descendants("item").ToList();

        // One rendered item: the dedup group collapsed the two sources' copies to a single result,
        // confirming the group actually formed (a split here would mean the fixture itself never
        // exercised AlternateMembers, and everything below would pass vacuously).
        var item = Assert.Single(items);
        var representativeGuid = item.Element("guid")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(representativeGuid), "The rendered item carried no guid.");

        // The registration this bead's fix performs — read back from the same DI-resolved singleton
        // SearchEndpoint just wrote to, rather than recomputed (see the class doc comment for why a
        // recomputation is racy under this assembly's parallel test execution).
        var releaseLookup = _factory.Services.GetRequiredService<InMemoryReleaseLookup>();
        var registered = releaseLookup.Snapshot();

        var representativeEntry = Assert.Single(registered, r => r.SourceName == RepresentativeSourceName);
        var memberEntry = Assert.Single(registered, r => r.SourceName == MemberSourceName);

        // The representative's guid resolves as it always did — this is the existing behaviour, not
        // what this bead changes, but pinning it here means a regression that broke the representative
        // path while "fixing" the member path would still fail this file.
        Assert.Equal(representativeEntry.ProxyGuid, representativeGuid);

        // The alternate member's ProxyGuid is never rendered on any response — this is what the
        // fix newly registers, read back from the lookup itself.
        var memberProxyGuid = memberEntry.ProxyGuid;

        using var memberDownload = await client.GetAsync(
            $"/download/{memberProxyGuid}?apikey={Uri.EscapeDataString(ApiKey)}");

        // Not a 404: pre-fix, the member's guid had no entry in either lookup tier and this would
        // have failed here.
        Assert.Equal(HttpStatusCode.OK, memberDownload.StatusCode);

        // THE DETECTABILITY ASSERTION (CLAUDE.md §4): grabbing successfully is not enough — a lookup
        // bug that resolved every guid to the REPRESENTATIVE's source would also return 200. Each
        // fake source's downloadPayload above is its own name, so the response body proves which
        // source's FetchDownloadAsync actually ran.
        var body = await memberDownload.Content.ReadAsStringAsync();
        Assert.Contains(MemberSourceName, body, StringComparison.Ordinal);
        Assert.DoesNotContain(RepresentativeSourceName, body, StringComparison.Ordinal);

        // The representative's own guid still resolves too (unchanged behaviour) — asserted last so
        // the member-specific checks above are the ones that would have failed on the old code.
        using var representativeDownload = await client.GetAsync(
            $"/download/{representativeGuid}?apikey={Uri.EscapeDataString(ApiKey)}");
        Assert.Equal(HttpStatusCode.OK, representativeDownload.StatusCode);
    }
}
