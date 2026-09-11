using System.Net;
using System.Xml.Linq;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-tps: the release lookup is a NEW PLACE A SECRET COULD COME TO REST. Before this change the
/// lookup lived only in process memory; now a serialized <c>ReleaseCandidate</c> is written to
/// <c>arbitarr.db</c> and survives restarts, so anything a candidate carries is now persisted
/// rather than merely resident. This holds the line on the one credential that could plausibly ride
/// along — a source API key embedded in an upstream release link.
///
/// <para><b>EVERY ABSENCE ASSERTION HERE HAS A POSITIVE CONTROL</b> (CLAUDE.md §4), and the
/// controls are the tests that make the rest bite. <c>Assert.DoesNotContain</c> passes just as
/// happily against an empty table or a secret that never reached the code under test, so each
/// control PLANTS an apikey-bearing value into the candidate's <c>Link</c> and demonstrates the
/// assertion WOULD fail on it. Asserting merely that a row exists would prove the secret EXISTS; it
/// would not prove it is DETECTABLE if it leaked. Reference shape:
/// <see cref="LogSecretInjectionTests"/>.</para>
/// </summary>
public sealed class ReleaseLookupPayloadSecretTests : IAsyncDisposable
{
    // "secret-api-key" is the prefix the pre-commit secret guard allowlists.
    private const string ClientKey = "secret-api-key-release-lookup-payload";

    /// <summary>
    /// The value standing in for a source API key embedded in an upstream link. Distinctive so a
    /// search across a payload, a guid or a log row cannot match it by accident.
    /// </summary>
    private const string UpstreamSourceKey = "secret-api-key-upstream-source-arbtps";

    private const string SourceName = "fake-hydra";

    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(), "arbitarr-release-lookup-secret-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebApplicationFactory<Program>> _factories = new();

    public ReleaseLookupPayloadSecretTests() => Directory.CreateDirectory(_configDirectory);

    /// <summary>
    /// A candidate whose link carries the upstream key in its query string — the realistic shape,
    /// since NZBHydra2's getnzb URLs carry an apikey parameter.
    /// </summary>
    private static ReleaseCandidate KeyBearingCandidate() => new()
    {
        Title = "Some.Release.S01E01.1080p.WEB-DL",
        Guid = "upstream-guid-secret-probe",
        PubDate = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero),
        Size = 1_234_567_890,
        Link = new Uri($"https://indexer.example.invalid/getnzb/abc?apikey={UpstreamSourceKey}"),
        Category = new[] { 5040 },
        Protocol = ProtocolKind.Usenet,
    };

    /// <summary>The same release with a clean link — what a correctly-configured source produces.</summary>
    private static ReleaseCandidate CleanCandidate() => new()
    {
        Title = "Some.Release.S01E01.1080p.WEB-DL",
        Guid = "upstream-guid-secret-probe",
        PubDate = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero),
        Size = 1_234_567_890,
        Link = new Uri("https://indexer.example.invalid/getnzb/abc"),
        Category = new[] { 5040 },
        Protocol = ProtocolKind.Usenet,
    };

    private WebApplicationFactory<Program> CreateHost(ReleaseCandidate candidate)
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
                    new IUpstreamSource[] { new SecondFakeUpstreamSource(SourceName, new[] { candidate }) });
            });
        });

        _factories.Add(factory);
        return factory;
    }

    /// <summary>
    /// THE POSITIVE CONTROL for <see cref="The_persisted_payload_and_guid_carry_no_source_apikey"/>.
    ///
    /// <para>It plants the key into the candidate's link, drives the SAME search, and asserts the
    /// persisted <c>PayloadJson</c> DOES contain it — proving the assertion in the test below is
    /// capable of failing, that the payload is where such a value would land, and that the search
    /// path really does reach this table. Without this, that test would pass against an empty
    /// table, against a payload that never held links at all, and against a search that never
    /// wrote a row.</para>
    ///
    /// <para>This documents a real property rather than a hypothetical one: the candidate's link is
    /// persisted VERBATIM, deliberately, because the source re-validates it at fetch time. So the
    /// guarantee this file asserts is not "the payload scrubs keys" — it is that Arbitarr never puts
    /// one there, because a source key lives only in the write-only <c>source:{id}:api_key</c>
    /// settings rows and is attached by the source's own HTTP client at fetch time.</para>
    /// </summary>
    [Fact]
    public async Task Positive_control_a_planted_key_in_the_link_IS_detectable_in_the_persisted_payload()
    {
        var host = CreateHost(KeyBearingCandidate());
        using var client = host.CreateClient();

        _ = await SearchAndExtractProxyGuidAsync(client);

        var payloads = await ReadPersistedPayloadsAsync(host);
        Assert.NotEmpty(payloads);
        Assert.Contains(payloads, p => p.Contains(UpstreamSourceKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// The real assertion, now that the control above has shown it can fail: with a normally
    /// configured source, neither the persisted payload nor the emitted proxy guid carries the
    /// source apikey.
    ///
    /// <para>The guid is asserted separately from the payload because it is the half that travels
    /// OUT — it goes into the download URL handed to Sonarr, which is logged by request-logging
    /// middleware and stored in the persistent log store. A guid derived from anything
    /// key-bearing would publish the key on a surface the payload's own privacy could not
    /// protect.</para>
    /// </summary>
    [Fact]
    public async Task The_persisted_payload_and_guid_carry_no_source_apikey()
    {
        var host = CreateHost(CleanCandidate());
        using var client = host.CreateClient();

        var proxyGuid = await SearchAndExtractProxyGuidAsync(client);

        var payloads = await ReadPersistedPayloadsAsync(host);

        // Load-bearing: "no payload contains the key" is vacuously true of an empty table, and the
        // control above is what proves a planted key would be found in these very strings.
        Assert.NotEmpty(payloads);

        foreach (var payload in payloads)
        {
            Assert.DoesNotContain(UpstreamSourceKey, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ClientKey, payload, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(UpstreamSourceKey, proxyGuid, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClientKey, proxyGuid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE POSITIVE CONTROL for <see cref="A_real_download_leaves_no_source_apikey_in_the_log_store"/>.
    ///
    /// <para>Plants the key into a log line through the REAL logger and asserts the stored row
    /// carries <see cref="LogMessageCleanser.Replacement"/> — proving the value reached the sink and
    /// was scrubbed there, rather than never having arrived. An assertion that the row merely exists
    /// would prove the secret EXISTS, not that it would be DETECTABLE if it leaked.</para>
    /// </summary>
    [Fact]
    public async Task Positive_control_a_planted_key_reaches_the_log_sink_and_is_redacted_there()
    {
        var host = CreateHost(CleanCandidate());
        using var client = host.CreateClient();
        _ = await client.GetAsync("/api/health");

        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Arbitarr.Test.ReleaseLookupLeakProbe");
        logger.LogWarning(
            "upstream fetch failed: https://indexer.example.invalid/getnzb/abc?apikey={ApiKey}",
            UpstreamSourceKey);

        await FlushLogSinkAsync();
        var probed = (await ReadLogEntriesAsync(host))
            .Where(entry => entry.Logger.Contains("ReleaseLookupLeakProbe", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(probed);
        foreach (var entry in probed)
        {
            Assert.DoesNotContain(UpstreamSourceKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Drives a REAL <c>/download/{proxyGuid}</c> — the route arb-tps changed — and asserts no
    /// persisted log row carries either key.
    ///
    /// <para>This route is newly interesting: it now performs a database read on a memory miss, and
    /// reconstitutes a candidate whose link it then hands to the source's HTTP client. Both are new
    /// places a link could be logged verbatim, and <c>IHttpClientFactory</c> attaches its own
    /// logging handler to every named client (CLAUDE.md §1). The download is driven twice — once
    /// warm, once after the memory tier has been emptied — so the store-backed path is exercised,
    /// not just the in-memory one.</para>
    /// </summary>
    [Fact]
    public async Task A_real_download_leaves_no_source_apikey_in_the_log_store()
    {
        var host = CreateHost(CleanCandidate());
        using var client = host.CreateClient();

        var proxyGuid = await SearchAndExtractProxyGuidAsync(client);

        // Warm (memory) and cold (store) paths, plus an error path: a request URL is likeliest to
        // be logged verbatim when something throws, so a happy-path-only probe would miss the
        // realistic leak.
        _ = await client.GetAsync($"/download/{Uri.EscapeDataString(proxyGuid)}?apikey={Uri.EscapeDataString(ClientKey)}");

        var memory = host.Services.GetRequiredService<Arbitarr.Api.Search.InMemoryReleaseLookup>();
        for (var i = 0; i <= Arbitarr.Api.Search.InMemoryReleaseLookup.MaxEntries; i++)
        {
            memory.Record(new Arbitarr.Api.Rendering.RenderedRelease(SourceName, new ReleaseCandidate
            {
                Title = $"Filler.{i}",
                Guid = $"filler-{i}",
                PubDate = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero),
                Size = 1,
                Link = new Uri($"https://indexer.example.invalid/getnzb/filler-{i}"),
                Protocol = ProtocolKind.Usenet,
            }));
        }
        Assert.Null(await memory.FindAsync(proxyGuid));

        _ = await client.GetAsync($"/download/{Uri.EscapeDataString(proxyGuid)}?apikey={Uri.EscapeDataString(ClientKey)}");
        _ = await client.GetAsync($"/download/not-a-real-guid?apikey={Uri.EscapeDataString(ClientKey)}");

        await FlushLogSinkAsync();
        var entries = await ReadLogEntriesAsync(host);

        // Without this the loop below is a no-op the day the sink stops recording anything — and
        // the control above is what proves a leak of this value WOULD be caught in these fields.
        Assert.NotEmpty(entries);

        // Every field, not just Message: Exception is the one most likely to carry a request URI.
        foreach (var entry in entries)
        {
            Assert.DoesNotContain(UpstreamSourceKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(UpstreamSourceKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ClientKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ClientKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Reads every persisted release-lookup payload, through a fresh scope.</summary>
    private static async Task<IReadOnlyList<string>> ReadPersistedPayloadsAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>()
            .ReleaseLookupEntries
            .AsNoTracking()
            .Select(e => e.PayloadJson)
            .ToListAsync();
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
            $"/newznab/api?t=search&q=some+release&apikey={Uri.EscapeDataString(ClientKey)}");
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

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked SQLite file on Windows must not fail the run.
        }
    }
}
