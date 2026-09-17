using System.Net;
using System.Xml.Linq;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Logging;
using Arbitarr.Integration.Tests.TestSupport;
using Arbitarr.Sources.Newznab;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-x7w8.13 — THE PROPERTY THE PROXY ACCESS MODE EXISTS FOR: Arbitarr fetches the payload using
/// the INDEXER's key and serves the bytes, so that key never leaves the process. This drives the
/// real <c>/download/{proxyGuid}</c> route over a real <see cref="NewznabSource"/> holding a planted
/// key, and asserts the key reaches no response header, no response body, no log row and no event.
///
/// <para><b>WHY THE KEY IS HELD BY THE ADAPTER RATHER THAN EMBEDDED IN THE LINK, and why that makes
/// this a different test from <see cref="ReleaseLookupPayloadSecretTests"/>.</b> That file covers a
/// key a hostile or misconfigured upstream put INSIDE a release link, which Arbitarr persists
/// verbatim. This one covers the key Arbitarr ITSELF holds for the source — the one the registry
/// reads per resolution from <c>source:{id}:api_key</c> and hands to the adapter that is about to
/// issue the request. Those are different values reaching the response by different routes, and
/// neither test's absence assertion says anything about the other's value.</para>
///
/// <para><b>THE REAL ADAPTER, NOT A FAKE, IS THE POINT.</b> A fake <see cref="IUpstreamSource"/>
/// holds no key and issues no request, so it cannot leak one — a test over a fake would assert the
/// absence of a value that was never in play, which is the vacuous shape CLAUDE.md §4 describes and
/// which has shipped three real leaks in this repository. <see cref="NewznabSource"/> is constructed
/// here with the planted key and a stub handler, so the key really is put into a real outbound
/// request URI, really does pass through <c>IHttpClientFactory</c>'s logging handler, and really
/// could reach every surface asserted below.</para>
///
/// <para><b>EVERY ABSENCE ASSERTION HERE HAS A POSITIVE CONTROL.</b>
/// <see cref="Positive_control_the_planted_indexer_key_is_detectable_on_every_surface_asserted"/>
/// puts the SAME key onto each of the four surfaces through that surface's own real write path and
/// demonstrates each assertion failing on it — so "the key is absent" below means the surface was
/// searched and found clean, not that the search could never have found anything.</para>
/// </summary>
public sealed class ProxyDownloadIndexerKeyNeverLeavesTheProcessTests : IAsyncLifetime
{
    /// <summary>The client key Sonarr/Radarr present to Arbitarr. Not the indexer's key.</summary>
    private const string ClientKey = "secret-api-key-proxy-client-x7w813";

    /// <summary>
    /// THE VALUE UNDER TEST: the key Arbitarr holds for the indexer and sends upstream. Distinctive
    /// enough that a substring search over a response body, a header, a log row or an event row
    /// cannot match it by accident, and prefixed as the pre-commit secret guard requires.
    /// </summary>
    private const string IndexerKey = "placeholder-indexer-key-proxy-x7w813";

    private const string SourceName = "direct-indexer";

    /// <summary>RFC 5737 TEST-NET-1: non-routable, so nothing here can reach a real host.</summary>
    private static readonly Uri IndexerBaseUrl = new("http://192.0.2.30:9117/");

    private const string PayloadText = "<nzb>proxy-served-bytes</nzb>";

    /// <summary>
    /// The per-CLASS root. Every host gets its OWN subdirectory under it (see
    /// <see cref="PerHostConfigDirectory"/>); this level exists so teardown has a single path to
    /// delete.
    /// </summary>
    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(), "arbitarr-proxy-indexer-key-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebApplicationFactory<Program>> _factories = new();

    public ProxyDownloadIndexerKeyNeverLeavesTheProcessTests() => Directory.CreateDirectory(_configDirectory);

    /// <summary>
    /// The feed this indexer answers a search with. The link is on the indexer's OWN origin, so it
    /// survives both the parse-time pin and <see cref="NewznabSource.FetchDownloadAsync"/>'s
    /// fetch-time re-pin (SEC-M1) — the download must actually happen for this test to be about
    /// anything.
    /// </summary>
    private static string Feed() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
          <item>
            <title>Proxy.Probe.S01E01.1080p.WEB-DL</title>
            <guid>proxy-probe-guid-1</guid>
            <link>{IndexerBaseUrl}getnzb/proxy-probe-1</link>
            <torznab:attr name="size" value="4096" />
          </item>
        </channel></rss>
        """;

    /// <summary>
    /// Answers the indexer's search with <see cref="Feed"/> and its download with the payload, and
    /// records every request URI so a test can prove the key really was sent upstream.
    /// </summary>
    private sealed class RecordingIndexerHandler : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);

            var isDownload = request.RequestUri!.AbsolutePath.Contains("getnzb", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = isDownload
                    ? new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(PayloadText))
                    : new StringContent(Feed(), System.Text.Encoding.UTF8, "application/xml"),
            });
        }
    }

    private RecordingIndexerHandler _handler = null!;

    /// <summary>
    /// A host whose single source is a REAL <see cref="NewznabSource"/> built with the planted key
    /// over a stub handler. Registered through <see cref="StaticSourceRegistry"/> — the integration
    /// seam arb-x7w8.4 added — because a source row would need a live indexer to be resolvable,
    /// while what this test needs is the real ADAPTER, which is what holds and sends the key.
    /// </summary>
    private WebApplicationFactory<Program> CreateHost()
    {
        _handler = new RecordingIndexerHandler();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // arb-j4hq: this host's OWN database, not one shared with the other hosts this class
            // builds. See PerHostConfigDirectory for why separate directories rather than disposing
            // the previous factory here.
            builder.UseSetting("Arbitarr:ConfigDir", PerHostConfigDirectory.Create(_configDirectory));
            builder.UseSetting("Arbitarr:ApiKey", ClientKey);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<ISourceRegistry>();

                // SCOPED, exactly as Program.cs registers it. IAsyncCircuitBreaker is scoped (it is
                // database-backed), so a singleton registration here would resolve it from the ROOT
                // provider and fault every request with a lifetime error — a 500 that looks like a
                // search bug rather than a test-wiring one.
                services.AddScoped<ISourceRegistry>(sp => new StaticSourceRegistry(
                    new IUpstreamSource[]
                    {
                        new NewznabSource(
                            new NewznabSourceOptions(
                                BaseUrl: IndexerBaseUrl,
                                ApiPath: "/api",
                                ApiKey: IndexerKey,
                                SourceName: SourceName,
                                RateLimitMaxCalls: 1000,
                                RateLimitInterval: TimeSpan.FromMilliseconds(1)),
                            new HttpClient(_handler),
                            sp.GetRequiredService<IAsyncCircuitBreaker>()),
                    }));
            });
        });

        _factories.Add(factory);
        return factory;
    }

    /// <summary>
    /// THE MAIN ASSERTION: a real proxy download serves the indexer's bytes, and the indexer key
    /// appears on none of the four surfaces a caller or an operator can read.
    ///
    /// <para>The four are asserted together rather than as four tests because they share one
    /// expensive setup and one control, and because the property is a conjunction: a key absent from
    /// the body but present in a header has still left the process.</para>
    /// </summary>
    [Fact]
    public async Task A_real_proxy_download_serves_bytes_and_leaks_the_indexer_key_to_no_surface()
    {
        var host = CreateHost();
        using var client = host.CreateClient();

        var proxyGuid = await SearchAndExtractProxyGuidAsync(client);

        using var response = await client.GetAsync(
            $"/download/{Uri.EscapeDataString(proxyGuid)}?apikey={Uri.EscapeDataString(ClientKey)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // PROXY MODE, NOT REDIRECT: the bytes themselves come back, and there is no Location header
        // for the client to follow — which is the only shape in which the indexer key could have
        // been handed over as part of a URL.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(PayloadText, body);
        Assert.Null(response.Headers.Location);

        // THE KEY REALLY WAS PUT INTO A REAL OUTBOUND REQUEST. Without this the four absences below
        // could all hold simply because no download ever happened, or because the adapter never sent
        // its key — the exact vacuity this whole file is shaped against. The search request is what
        // carries the key (in its query string, per NewznabSource.AppendApiKey); the download request
        // is the origin-pinned link, which the proxy then serves as bytes.
        Assert.Single(_handler.RequestedUris, uri => uri.AbsolutePath.Contains("getnzb", StringComparison.Ordinal));
        Assert.Contains(_handler.RequestedUris, uri => uri.Query.Contains(IndexerKey, StringComparison.Ordinal));

        // SURFACE 1: every response header, name and value, on the download response.
        AssertNoHeaderCarriesTheKey(response.Headers.Concat(response.Content.Headers));

        // SURFACE 2: the response body.
        AssertBodyDoesNotCarryTheKey(body);

        // SURFACE 3: the persistent log store, PER ROW. "Some row is clean" would pass against an
        // implementation that redacted one row and leaked on every other one.
        await FlushLogSinkAsync();
        var entries = await ReadLogEntriesAsync(host);
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.DoesNotContain(IndexerKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(IndexerKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(IndexerKey, entry.Logger, StringComparison.OrdinalIgnoreCase);
        }

        // SURFACE 4: the events table behind the un-gated /api/activity feed, per row and per field.
        // A successful download records nothing, so this also pins that a routine grab does not
        // start writing operator-visible rows that could carry the key.
        var events = await ReadEventFieldsAsync(host);
        foreach (var field in events)
        {
            Assert.DoesNotContain(IndexerKey, field, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// THE POSITIVE CONTROL (CLAUDE.md §4), and the test that gives the four absences above their
    /// meaning. It puts the SAME key onto each of the four surfaces through that surface's own real
    /// write path and asserts each is DETECTABLE there — so a leak of this value would be caught by
    /// exactly the assertions above, rather than those assertions passing because the key could
    /// never have been found in those fields at all.
    ///
    /// <para><b>The log surface's control asserts the REPLACEMENT is present, not merely that the
    /// key is absent.</b> That is the stronger statement and the one
    /// <see cref="LogSecretInjectionTests"/> establishes as the reference: it proves the value
    /// reached <see cref="LogMessageCleanser"/> and was scrubbed there, where a bare absence would
    /// also hold if the line had never been written.</para>
    ///
    /// <para><b>Nothing vulnerable is added to the repository to do this.</b> Each control writes
    /// the key through an ordinary, already-existing API — a header on a response the test itself
    /// builds, a string it searches, a real logger, a real event sink — rather than mutating the
    /// production path. The mutation evidence for the download path itself was taken in a throwaway
    /// project outside the working tree.</para>
    /// </summary>
    [Fact]
    public async Task Positive_control_the_planted_indexer_key_is_detectable_on_every_surface_asserted()
    {
        var host = CreateHost();
        using var client = host.CreateClient();
        _ = await client.GetAsync("/api/health");

        // CONTROL 1 — HEADERS. Feeds a response carrying the key in a header through the SAME
        // helper the main test's header scan calls, so a change to that helper's enumeration
        // invalidates this control too, rather than the control drifting from what is actually
        // asserted above.
        using var leakyResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("body"),
        };
        leakyResponse.Headers.Add("X-Upstream-Key", IndexerKey);
        Assert.Throws<Xunit.Sdk.DoesNotContainException>(
            () => AssertNoHeaderCarriesTheKey(leakyResponse.Headers.Concat(leakyResponse.Content.Headers)));

        // CONTROL 2 — BODY. Feeds a body carrying the key through the SAME helper the main test's
        // body assertion calls.
        var leakyBody = $"<nzb url=\"{IndexerBaseUrl}getnzb/1?apikey={IndexerKey}\" />";
        Assert.Throws<Xunit.Sdk.DoesNotContainException>(() => AssertBodyDoesNotCarryTheKey(leakyBody));

        // CONTROL 3 — LOG STORE. Written through the REAL logger, so this proves the sink is wired
        // and that the cleanser ran on the way in, rather than that the line never arrived.
        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Arbitarr.Test.ProxyIndexerKeyLeakProbe");
        logger.LogWarning(
            "download fetch failed: {Url}",
            $"{IndexerBaseUrl}getnzb/proxy-probe-1?apikey={IndexerKey}");

        await FlushLogSinkAsync();
        var probed = (await ReadLogEntriesAsync(host))
            .Where(entry => entry.Logger.Contains("ProxyIndexerKeyLeakProbe", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(probed);
        foreach (var entry in probed)
        {
            Assert.DoesNotContain(IndexerKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal);
        }

        // CONTROL 4 — EVENTS. Written through the REAL sink, so this proves the events table is
        // reachable from this host, that its fields are what the assertion above reads, and that a
        // key placed in one WOULD be found there.
        await host.Services.GetRequiredService<Arbitarr.Core.Diagnostics.IEventSink>().RecordAsync(
            Arbitarr.Core.Diagnostics.RecordedEventKind.SourceFailed,
            summary: $"probe: download refused carrying apikey={IndexerKey}",
            reason: null,
            sourceDisplayName: null,
            cancellationToken: CancellationToken.None);

        var fields = await ReadEventFieldsAsync(host);
        Assert.NotEmpty(fields);
        Assert.Contains(fields, field => field.Contains(IndexerKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// SURFACE 1's assertion, shared by the main test and by CONTROL 1 so the two can never drift:
    /// a change to this enumeration changes what both check.
    /// </summary>
    private static void AssertNoHeaderCarriesTheKey(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var header in headers)
        {
            Assert.DoesNotContain(IndexerKey, header.Key, StringComparison.OrdinalIgnoreCase);
            foreach (var value in header.Value)
            {
                Assert.DoesNotContain(IndexerKey, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// SURFACE 2's assertion, shared by the main test and by CONTROL 2.
    /// </summary>
    private static void AssertBodyDoesNotCarryTheKey(string body)
    {
        Assert.DoesNotContain(IndexerKey, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every string field of every event row — Summary, Reason, SourceDisplayName and Detail. All
    /// four rather than Summary alone: the assertion exists to find a key ANYWHERE an operator can
    /// read it on <c>/api/activity</c>, and checking one field would look thorough while leaving the
    /// other three unexamined.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadEventFieldsAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>()
            .Events
            .AsNoTracking()
            .Select(e => new { e.Summary, e.Reason, e.SourceDisplayName, e.Detail })
            .ToListAsync();

        return rows
            .SelectMany(r => new[] { r.Summary, r.Reason, r.SourceDisplayName, r.Detail })
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
    }

    /// <summary>
    /// EVERY row, not the first page of them (arb-j4hq). This read used to take page 1 at
    /// <see cref="LogStore.MaxPageSize"/>, which silently bounded every absence assertion in this
    /// file at 200 rows: a leaked row landing past that boundary would be invisible to a scan that
    /// never asked for it, and the test would stay green while covering less than it claims. The
    /// loop is bounded by <see cref="LogPage.Total"/>, which the store computes in the same
    /// transaction as the page, so it terminates even while the sink is still appending. Kept
    /// identical to the copy in <see cref="RedirectAccessModeKeyNeverReachesLogsTests"/> so the two
    /// sibling files cannot drift in what they scan.
    /// </summary>
    private static async Task<IReadOnlyList<LogEntry>> ReadLogEntriesAsync(WebApplicationFactory<Program> host)
    {
        var store = host.Services.GetRequiredService<LogStore>();
        var entries = new List<LogEntry>();

        for (var page = 1; ; page++)
        {
            var read = await store.ReadAsync(level: null, logger: null, page: page, pageSize: LogStore.MaxPageSize);
            entries.AddRange(read.Entries);

            if (read.Entries.Count == 0 || entries.Count >= read.Total)
            {
                return entries;
            }
        }
    }

    private static async Task<string> SearchAndExtractProxyGuidAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            $"/newznab/api?t=search&q=proxy+probe&apikey={Uri.EscapeDataString(ClientKey)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var item = Assert.Single(XDocument.Parse(body).Descendants("item"));
        var enclosureUrl = item.Elements("enclosure").Single().Attribute("url")!.Value;

        var path = new Uri(enclosureUrl).AbsolutePath;
        return Uri.UnescapeDataString(path["/download/".Length..]);
    }

    /// <summary>
    /// The sink batches on a fixed interval by design (it must never write on the caller's thread),
    /// so a read taken immediately after a request can legitimately see nothing yet.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        ConfigDirectoryTeardown.Delete(_configDirectory);
    }
}
