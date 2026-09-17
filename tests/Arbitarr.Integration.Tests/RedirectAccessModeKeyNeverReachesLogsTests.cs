using System.Net;
using System.Xml.Linq;
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
/// arb-x7w8.14 — THE GUARD THE REDIRECT ACCESS MODE IS ALLOWED TO EXIST BEHIND. In redirect mode
/// Arbitarr answers <c>/download/{proxyGuid}</c> with a 302 whose <c>Location</c> is the indexer's
/// own URL, and that URL NECESSARILY carries the indexer's API key, because the indexer put it there
/// when it generated the search result. The disclosure to the caller is the mode's purpose and the
/// operator's explicit choice. What is NOT permitted is that same key reaching a log row, an event
/// or a health item — surfaces the operator never opted in to and, in the case of
/// <c>/api/activity</c> and <c>/api/status</c>, ones that are un-gated.
///
/// <para><b>READ THIS BEFORE CONCLUDING THIS TEST IS POINTLESS.</b> Nothing in today's inbound
/// pipeline logs a response header: <c>UseHttpLogging</c>/<c>AddHttpLogging</c> is registered
/// nowhere in this application, and the redirect arm writes no log line of its own. So the absence
/// assertions below are about a surface that is CURRENTLY SILENT — exactly the shape CLAUDE.md §4
/// warns is most likely to be vacuous, which is why the positive control is not optional here. The
/// risk this defends against is FUTURE: someone adds <c>app.UseHttpLogging()</c> with
/// <c>HttpLoggingFields.ResponseHeaders</c>, or writes an <c>ILogger</c> line on the redirect arm
/// the way NZBHydra2's <c>FileHandler</c> does ("Redirecting to {}", i.e. it logs the key). THIS
/// TEST is the ratchet that turns either into a red build. Do not delete it on the grounds that
/// today's pipeline is clean; that is the state it exists to preserve.</para>
///
/// <para><b>NO EXISTING LAYER MAKES THIS SAFE, and the reasons differ per layer.</b>
/// <c>DisableUriRedaction</c> governs <c>IHttpClientFactory</c>'s collapse of an OUTBOUND request
/// URI's query to <c>?*</c> — it is about a URI Arbitarr REQUESTS, and in redirect mode Arbitarr
/// issues no outbound fetch at all, so that path does not even execute. <c>LogMessageCleanser</c>'s
/// shared <c>CredentialPatterns</c> arms scrub credential-shaped values BY PATTERN wherever they
/// appear, so they DO catch an <c>apikey=</c> parameter inside a logged <c>Location</c> — which is
/// precisely why leaning on them here would be wrong: it makes the defence contingent on the leak
/// wearing a shape the denylist already knows, and the cleanser's own doc calls itself defence in
/// depth rather than the control. <c>.RemoveAllLoggers()</c> is a third wrong instrument: its one
/// real call site is the webhook notification client, whose token rides in a URL PATH, and a
/// response header never passes through that handler in the first place. The mechanism here is that
/// NO LINE IS WRITTEN, and this test is what holds that true.</para>
///
/// <para><b>THE ASSERTIONS SEARCH FOR TWO VALUES, AND MUTATION TESTING IS WHY.</b> Searching for
/// the KEY alone is not enough and was measured not to be: against an implementation that logs the
/// <c>Location</c> the way NZBHydra2 does, the key-only assertions PASSED, because
/// <c>LogMessageCleanser</c>'s <c>apikey=</c> arm scrubbed that parameter out of the logged
/// <c>Location</c> before the row was written. That is a true statement about the cleanser and says
/// nothing whatever about this arm — the whole Location was still being logged. So every scan
/// also looks for <see cref="LinkPathMarker"/> — the link's path segment, which matches no
/// <c>CredentialPatterns</c> arm and is therefore stored verbatim if anything logs the Location. The
/// marker assertion is the one that bites; the key assertion is kept because the key is what the
/// bead is ultimately about and its presence in the <c>Location</c> is the mode's defining
/// property.</para>
///
/// <para><b>Asserted PER MODE, because a test exercising one proves nothing about the other</b>
/// (the bead is explicit on this). Redirect mode asserts the key IS in the <c>Location</c> — the
/// positive statement that the mode does what it claims, which doubles as proof that this file's
/// key-detection actually detects this key in this shape. Proxy mode asserts it is in no response
/// at all. Both modes assert it is in no log row (PER ROW, per CLAUDE.md §4), no event and no health
/// item.</para>
///
/// <para>This is the redirect-mode sibling of
/// <see cref="ProxyDownloadIndexerKeyNeverLeavesTheProcessTests"/>, which covers the same property
/// for proxy mode over the same real adapter. Neither file's assertions say anything about the
/// other's mode.</para>
/// </summary>
public sealed class RedirectAccessModeKeyNeverReachesLogsTests : IAsyncLifetime
{
    /// <summary>The client key Sonarr/Radarr present to Arbitarr. Not the indexer's key.</summary>
    private const string ClientKey = "secret-api-key-redirect-client-x7w814";

    /// <summary>
    /// THE VALUE UNDER TEST: the indexer's own key, as it appears inside the release link the indexer
    /// generated. Distinctive enough that a substring search over a header, a body, a log row or an
    /// event row cannot match it by accident, and prefixed as the pre-commit secret guard requires.
    /// </summary>
    private const string IndexerKey = "placeholder-indexer-key-redirect-x7w814";

    private const string SourceName = "redirect-indexer";

    /// <summary>RFC 5737 TEST-NET-1: non-routable, so nothing here can reach a real host.</summary>
    private static readonly Uri IndexerBaseUrl = new("http://192.0.2.40:9117/");

    /// <summary>
    /// The download link exactly as a real indexer emits it — WITH THE KEY ALREADY IN ITS QUERY
    /// STRING. That is the shape the whole bead is about: redirect mode passes this link through
    /// unchanged, appending nothing, because the key is already in it. A link without a key would
    /// make every assertion below vacuous, so this is the single most load-bearing constant here.
    /// </summary>
    private static readonly string DownloadLink =
        $"{IndexerBaseUrl}getnzb/{LinkPathMarker}?apikey={IndexerKey}";

    /// <summary>
    /// THE PATH SEGMENT OF THE LINK, AND THE REASON THIS FILE IS NOT VACUOUS. It is distinctive, it
    /// is part of the <c>Location</c> value, and — unlike the <c>apikey=</c> query parameter — it
    /// matches NO <c>CredentialPatterns</c> arm, so nothing redacts it on the way into the log store.
    ///
    /// <para><b>This was established by mutation, not by reasoning.</b> An implementation that logs
    /// the Location the way NZBHydra2's <c>FileHandler</c> does was built in a throwaway copy outside
    /// the repository, and the key-only assertions PASSED against it: <c>LogMessageCleanser</c>
    /// scrubbed the <c>?apikey=</c> query string before the row was written, so "the key is in no log
    /// row" held while the Location was being logged on every download. That is a true statement
    /// about the cleanser and says NOTHING about the redirect arm — precisely the vacuity CLAUDE.md
    /// §4 describes, and it is why the assertion below searches for the path marker as well.</para>
    ///
    /// <para>Asserting on this marker is what makes the test about the property the bead actually
    /// requires: that the redirect arm writes NO LOG LINE AT ALL. The cleanser is defence in depth
    /// underneath it, not the control — and a Location in some other shape (a path-borne token, a
    /// differently-named parameter) would not be scrubbed at all.</para>
    /// </summary>
    private const string LinkPathMarker = "redirect-probe-location-marker-1";

    private const string PayloadText = "<nzb>redirect-probe-bytes</nzb>";

    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(), "arbitarr-redirect-access-mode-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebApplicationFactory<Program>> _factories = new();

    public RedirectAccessModeKeyNeverReachesLogsTests() => Directory.CreateDirectory(_configDirectory);

    /// <summary>
    /// The feed this indexer answers a search with. The link is on the indexer's OWN origin so it
    /// survives the parse-time pin, and it carries the key exactly as the real thing would.
    /// </summary>
    private static string Feed() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel>
          <item>
            <title>Redirect.Probe.S01E01.1080p.WEB-DL</title>
            <guid>redirect-probe-guid-1</guid>
            <link>{DownloadLink}</link>
            <torznab:attr name="size" value="4096" />
          </item>
        </channel></rss>
        """;

    /// <summary>
    /// Answers the indexer's search with <see cref="Feed"/> and any download with the payload, and
    /// records every request URI so a test can prove whether an outbound fetch happened at all.
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
    /// A host whose single source is a REAL <see cref="NewznabSource"/> built with the planted key,
    /// resolved through a <see cref="StaticSourceRegistry"/> carrying the access mode under test.
    ///
    /// <para><b>The real adapter rather than a fake is the point</b>, for the reason
    /// <see cref="ProxyDownloadIndexerKeyNeverLeavesTheProcessTests"/> states: a fake holds no key
    /// and issues no request, so it could not leak one, and a test over it would assert the absence
    /// of a value that was never in play — the vacuous shape that shipped three real leaks here.</para>
    ///
    /// <para>SCOPED, exactly as <c>Program.cs</c> registers it: <c>IAsyncCircuitBreaker</c> is scoped
    /// (it is database-backed), so a singleton registration would resolve it from the ROOT provider
    /// and fault every request with a lifetime error that reads like a search bug.</para>
    /// </summary>
    private WebApplicationFactory<Program> CreateHost(string accessMode)
    {
        _handler = new RecordingIndexerHandler();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);
            builder.UseSetting("Arbitarr:ApiKey", ClientKey);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<ISourceRegistry>();

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
                    },
                    new Dictionary<string, string>(StringComparer.Ordinal) { [SourceName] = accessMode }));
            });
        });

        _factories.Add(factory);
        return factory;
    }

    /// <summary>
    /// REDIRECT MODE: the caller is sent to the indexer's link — key and all — and that key reaches
    /// no log row, no event and no health item.
    ///
    /// <para>The <c>Location</c> assertion is POSITIVE and comes first on purpose. It proves the mode
    /// does what it claims, and it is simultaneously the control for everything after it: a key that
    /// is demonstrably present in this shape, found by this comparison, is a key the absence
    /// assertions below were genuinely capable of finding.</para>
    ///
    /// <para>The redirect is <b>not followed</b> — <c>AllowAutoRedirect</c> is off — because
    /// following it would replace the 302 under test with the indexer's 200 and there would be no
    /// <c>Location</c> left to assert on.</para>
    /// </summary>
    [Fact]
    public async Task Redirect_mode_hands_the_key_to_the_caller_and_to_no_log_row_event_or_health_item()
    {
        var host = CreateHost("Redirect");
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var proxyGuid = await SearchAndExtractProxyGuidAsync(client);

        using var response = await client.GetAsync(
            $"/download/{Uri.EscapeDataString(proxyGuid)}?apikey={Uri.EscapeDataString(ClientKey)}");

        // THE MODE DID WHAT IT CLAIMS: a 302 at the indexer's own link, passed through unchanged.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal(DownloadLink, location.OriginalString);

        // THE POSITIVE ASSERTION THE BEAD REQUIRES, and this file's own control: the key IS present
        // in the Location header. Every absence assertion below searches for the same value with the
        // same comparison, so this proves those searches can find it when it is there.
        Assert.Contains(IndexerKey, location.OriginalString, StringComparison.Ordinal);

        // NO OUTBOUND FETCH HAPPENED. The search request is the only thing that reached the handler;
        // the adapter was never asked for the payload, which is what "branches before the adapter"
        // means in practice and why the outbound-URI layers do not apply here.
        Assert.DoesNotContain(_handler.RequestedUris, uri => uri.AbsolutePath.Contains("getnzb", StringComparison.Ordinal));

        // The body is empty on a redirect, but assert it anyway rather than assume: a framework
        // change that started rendering the target into the body would be a leak by another route.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(IndexerKey, body, StringComparison.OrdinalIgnoreCase);

        await AssertKeyReachesNoLogRowEventOrHealthItemAsync(host, client);
    }

    /// <summary>
    /// PROXY MODE, asserted in this file rather than only in the proxy file, because the bead requires
    /// both modes to be covered wherever the claim is made per-mode: the key is in NO response at all
    /// — no header, no body — and in no log row, event or health item.
    ///
    /// <para>The contrast with the test above is what makes each meaningful. A single mode's test
    /// could pass against an implementation that ignored the mode entirely and always did one thing;
    /// running the same host, the same adapter and the same planted key through both branches is what
    /// shows the branch is real.</para>
    /// </summary>
    [Fact]
    public async Task Proxy_mode_serves_the_bytes_and_hands_the_key_to_nothing_at_all()
    {
        var host = CreateHost("Proxy");
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var proxyGuid = await SearchAndExtractProxyGuidAsync(client);

        using var response = await client.GetAsync(
            $"/download/{Uri.EscapeDataString(proxyGuid)}?apikey={Uri.EscapeDataString(ClientKey)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Bytes, and NO Location for the client to follow — the only shape in which the key could
        // have been handed over as part of a URL.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(PayloadText, body);
        Assert.Null(response.Headers.Location);

        // The fetch really happened and really carried the key upstream. Without this the absences
        // below could hold simply because no download occurred.
        Assert.Contains(_handler.RequestedUris, uri => uri.AbsolutePath.Contains("getnzb", StringComparison.Ordinal));

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            Assert.DoesNotContain(IndexerKey, header.Key, StringComparison.OrdinalIgnoreCase);
            foreach (var value in header.Value)
            {
                Assert.DoesNotContain(IndexerKey, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.DoesNotContain(IndexerKey, body, StringComparison.OrdinalIgnoreCase);

        await AssertKeyReachesNoLogRowEventOrHealthItemAsync(host, client);
    }

    /// <summary>
    /// THE POSITIVE CONTROL (CLAUDE.md §4), and the test that gives every absence above its meaning.
    /// It puts the SAME key onto each of the three shared surfaces through that surface's own real
    /// write path and asserts each is DETECTABLE there.
    ///
    /// <para><b>Why this file needs it more than most.</b> The redirect arm writes no log line, so
    /// "no log row contains the key" would pass against a reader that searched the wrong table, a
    /// sink that recorded nothing, or an assertion that could never match this value — an empty set
    /// contains nothing. Three PRs here (#57, #78, #80) shipped exactly that shape and only mutation
    /// testing caught them.</para>
    ///
    /// <para><b>The log control asserts the REPLACEMENT is PRESENT, not merely that the key is
    /// absent</b>, which is the stronger statement and the one <see cref="LogSecretInjectionTests"/>
    /// establishes as the reference: it proves the value reached <c>LogMessageCleanser</c> and was
    /// scrubbed there, where a bare absence would also hold if the line had never arrived.</para>
    ///
    /// <para><b>Nothing vulnerable is added to the repository to do this.</b> Each control writes the
    /// key through an ordinary existing API — a real logger, a real event sink, the real refusal
    /// tracker — rather than mutating the production path. The mutation evidence for the redirect arm
    /// itself was taken in a throwaway project outside the working tree: an implementation logging
    /// the Location header at Information on that arm, against which
    /// <see cref="Redirect_mode_hands_the_key_to_the_caller_and_to_no_log_row_event_or_health_item"/>
    /// fails.</para>
    /// </summary>
    [Fact]
    public async Task Positive_control_the_planted_indexer_key_is_detectable_on_every_surface_asserted()
    {
        var host = CreateHost("Redirect");
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _ = await client.GetAsync("/api/health");

        // CONTROL 1 — LOG STORE. Written through the REAL logger, so this proves the sink is wired,
        // that the fields the assertion reads are the fields a line lands in, and that the cleanser
        // ran on the way in rather than the line never arriving.
        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Arbitarr.Test.RedirectLocationLeakProbe")
            .LogWarning("Redirecting to {Location}", DownloadLink);

        await FlushLogSinkAsync();
        var probed = (await ReadLogEntriesAsync(host))
            .Where(entry => entry.Logger.Contains("RedirectLocationLeakProbe", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(probed);
        foreach (var entry in probed)
        {
            // The KEY reached the cleanser and was scrubbed — the LogSecretInjectionTests-style
            // statement, stronger than a bare absence because it proves the line actually arrived.
            Assert.DoesNotContain(IndexerKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal);

            // AND THE CONTROL THAT MATTERS MOST: the LOCATION survives into the stored row intact.
            // Nothing redacts the path, so if the redirect arm ever logs the Location, THIS is the
            // value that lands in the table — and this assertion proves the main tests' per-row scan
            // would find it. Without this the file would be asserting the absence of something the
            // cleanser removes anyway, which is exactly what mutation testing caught it doing.
            Assert.Contains(LinkPathMarker, entry.Message, StringComparison.Ordinal);
        }

        // CONTROL 2 — EVENTS. Written through the REAL sink, so this proves the events table is
        // reachable from this host, that its fields are the ones the assertion reads, and that a key
        // placed in one WOULD be found by exactly that scan.
        await host.Services.GetRequiredService<Arbitarr.Core.Diagnostics.IEventSink>().RecordAsync(
            Arbitarr.Core.Diagnostics.RecordedEventKind.SourceFailed,
            summary: $"probe: redirected to {DownloadLink}",
            reason: null,
            sourceDisplayName: null,
            cancellationToken: CancellationToken.None);

        var fields = await ReadEventFieldsAsync(host);
        Assert.NotEmpty(fields);
        Assert.Contains(fields, field => field.Contains(IndexerKey, StringComparison.Ordinal));
        Assert.Contains(fields, field => field.Contains(LinkPathMarker, StringComparison.Ordinal));

        // CONTROL 3 — HEALTH ITEMS on /api/status. Written through the REAL tracker, proving the
        // endpoint renders a refusal reason into the response at all — so the main tests' scan of
        // that body is searching something that can carry text, not an always-empty document.
        await host.Services.GetRequiredService<Arbitarr.Core.Diagnostics.IDownloadRefusalTracker>()
            .RecordRefusalAsync(
                SourceName,
                $"probe: refused after redirect to {DownloadLink}",
                DateTimeOffset.UnixEpoch,
                CancellationToken.None);

        var status = await client.GetStringAsync("/api/status");
        Assert.Contains(IndexerKey, status, StringComparison.Ordinal);
        Assert.Contains(LinkPathMarker, status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three surfaces both modes must keep clean, asserted the same way for each so neither mode's
    /// coverage can drift from the other's — and so the positive control above is provably the control
    /// for both.
    /// </summary>
    private static async Task AssertKeyReachesNoLogRowEventOrHealthItemAsync(
        WebApplicationFactory<Program> host,
        HttpClient client)
    {
        // SURFACE 1: the persistent log store, PER ROW and per field. Per row because "some row is
        // clean" still passes against an implementation that redacted one row and leaked on every
        // other one (CLAUDE.md §4). Per field because the exception text is the field most likely to
        // carry a URI, and checking only Message would look thorough while leaving the likeliest leak
        // unexamined.
        await FlushLogSinkAsync();
        var entries = await ReadLogEntriesAsync(host);

        // The table is non-empty, so the per-row loop below is not iterating over nothing. This is
        // what stops the whole assertion becoming a no-op the day the sink stops recording.
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            Assert.DoesNotContain(IndexerKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(IndexerKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(IndexerKey, entry.Logger, StringComparison.OrdinalIgnoreCase);

            // AND THE LINK ITSELF, via its unscrubbable path marker. The three assertions above are
            // about the KEY and are satisfied by LogMessageCleanser alone — mutation proved it: an
            // implementation logging the whole Location passes them, because the cleanser strips the
            // ?apikey= query string on the way in. This one is about the LOCATION, which no pattern
            // redacts, and it is therefore the assertion that actually says the redirect arm is
            // silent. See LinkPathMarker.
            Assert.DoesNotContain(LinkPathMarker, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LinkPathMarker, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // SURFACE 2: every string field of every event row behind the un-gated /api/activity feed.
        foreach (var field in await ReadEventFieldsAsync(host))
        {
            Assert.DoesNotContain(IndexerKey, field, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(LinkPathMarker, field, StringComparison.OrdinalIgnoreCase);
        }

        // SURFACE 3: /api/status, which is PublicRead and renders the sticky per-source health items.
        // Asserted over the whole rendered body rather than over the tracker's snapshot: the body is
        // what an unauthenticated caller can actually read, and a projection that derived new text
        // from a refusal would show up here and nowhere else.
        var status = await client.GetStringAsync("/api/status");
        Assert.DoesNotContain(IndexerKey, status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LinkPathMarker, status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every string field of every event row — Summary, Reason, SourceDisplayName and Detail. All four
    /// rather than Summary alone: the assertion exists to find a key ANYWHERE an operator can read it
    /// on <c>/api/activity</c>.
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

    private static async Task<IReadOnlyList<LogEntry>> ReadLogEntriesAsync(WebApplicationFactory<Program> host)
    {
        var page = await host.Services.GetRequiredService<LogStore>()
            .ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);
        return page.Entries;
    }

    private static async Task<string> SearchAndExtractProxyGuidAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            $"/newznab/api?t=search&q=redirect+probe&apikey={Uri.EscapeDataString(ClientKey)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var item = Assert.Single(XDocument.Parse(body).Descendants("item"));
        var enclosureUrl = item.Elements("enclosure").Single().Attribute("url")!.Value;

        var path = new Uri(enclosureUrl).AbsolutePath;
        return Uri.UnescapeDataString(path["/download/".Length..]);
    }

    /// <summary>
    /// The sink batches on a fixed interval by design (it must never write on the caller's thread), so
    /// a read taken immediately after a request can legitimately see nothing yet.
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
