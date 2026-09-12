using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Logging;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #65 plan §2/§5 — THE GUARD THAT ACTUALLY HOLDS THE LINE. A known API key is put through the real
/// search pipeline, and this asserts it appears in NO log row.
///
/// This is the primary control, not <see cref="LogMessageCleanser"/>. The cleanser is a denylist,
/// and a denylist only ever catches shapes somebody already watched leak — Sonarr's equivalent took
/// a decade of production incidents to accumulate, and Arbitarr would be starting that clock at
/// zero while already holding source API keys and (via #57) webhook URLs. What Arbitarr has instead
/// is a structural posture: secrets are not logged in the first place, because
/// <c>source:{id}:api_key</c> is unreachable from the <c>SettingsCatalog</c> projection,
/// <c>HasApiKeyAsync</c> returns a bool, and <c>SourceProbeOutcome</c> is a closed enum with no
/// string field precisely so a probe failure cannot carry key-derived text.
///
/// A posture is only real if something fails when it is broken. This is that something. It is the
/// direct sibling of <see cref="SearchRecentLogTests"/>, which asserts the same key never reaches a
/// response body; this asserts it never reaches the log database either.
///
/// The apikey travels on the raw query string, which is the single most likely thing to be logged
/// verbatim by request-logging middleware — so this exercises the realistic leak path, not a
/// hypothetical one. If a future change starts logging request URLs, THIS TEST is what fails.
/// </summary>
public sealed class LogSecretInjectionTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    // "secret-api-key" is the value the pre-commit secret guard allowlists (and the one
    // SearchRecentLogTests already uses); the suffix keeps it distinctive when searching log rows.
    private const string ApiKey = "secret-api-key-injection-probe";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configDirectory;

    public LogSecretInjectionTests(WebApplicationFactory<Program> factory)
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-log-injection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "log-injection-fake-source",
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Log Injection Probe Release",
                            Guid = "log-injection-probe-1",
                            PubDate = DateTimeOffset.UtcNow,
                            Size = 123_456,
                            Link = new Uri("http://192.0.2.70:8080/getnzb/log-injection-probe-1"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Usenet,
                        },
                    }));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
            });
        });
    }

    /// <summary>
    /// This class builds its own config directory, so it owns deleting it (arb-gphi).
    /// <see cref="ConfigDirectoryTeardown"/> carries why the pool clear and the delete are both
    /// required — and this class's directory holds the LOG database the assertions read, so the
    /// clear has to cover that second database too.
    /// </summary>
    public void Dispose() => ConfigDirectoryTeardown.Delete(_configDirectory);

    [Fact]
    public async Task An_apikey_driven_through_the_search_pipeline_appears_in_no_log_row()
    {
        using var client = _factory.CreateClient();

        // Drive several real requests carrying the key, including ones that fail: error paths are
        // where a request URL is most likely to be logged verbatim, so a happy-path-only probe
        // would miss the realistic leak.
        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q=log.injection.probe&apikey={Uri.EscapeDataString(ApiKey)}");
        searchResponse.EnsureSuccessStatusCode();

        await client.GetAsync($"/torznab/api?t=caps&apikey={Uri.EscapeDataString(ApiKey)}");
        await client.GetAsync($"/newznab/api?t=search&q=probe&apikey={Uri.EscapeDataString(ApiKey)}");
        // A deliberately malformed request with the key attached — the error path.
        await client.GetAsync($"/torznab/api?t=notarealmode&apikey={Uri.EscapeDataString(ApiKey)}");

        var store = _factory.Services.GetRequiredService<LogStore>();
        await FlushLogSinkAsync();

        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        // A "no row contains the key" assertion passes trivially against an empty table, which
        // would make this whole test a no-op the day the sink stops recording anything. Prove the
        // pipeline actually logged first, so the assertion below has something to be true of.
        Assert.NotEmpty(page.Entries);

        // Assert over EVERY field an entry has, not just the message: the exception text is the
        // field most likely to carry a request URI, and checking only Message would let the most
        // probable leak through while looking thorough.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(ApiKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiKey, entry.Logger, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task An_apikey_logged_by_mistake_is_redacted_before_it_is_stored()
    {
        // The defence-in-depth layer, asserted end to end rather than only as a unit test of the
        // cleanser: this proves the cleanser is actually WIRED INTO the sink's write path. A
        // correct cleanser that nothing calls is the failure mode a unit test cannot see.
        using var client = _factory.CreateClient();
        _ = await client.GetAsync("/api/health");

        var store = _factory.Services.GetRequiredService<LogStore>();
        var logger = _factory.Services
            .GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
            .CreateLogger("Arbitarr.Test.LeakySite");

        logger.LogWarning(
            "upstream request failed: http://192.0.2.10:5076/api?t=search&apikey={0}",
            ApiKey);

        await FlushLogSinkAsync();

        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);
        var leaky = page.Entries.Where(e => e.Logger.Contains("LeakySite", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(leaky);
        foreach (var entry in leaky)
        {
            Assert.DoesNotContain(ApiKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Waits for the sink's background pump to drain. The provider batches on a 500 ms interval by
    /// design (AC5 — it must never write on the caller's thread), so a read taken immediately after
    /// a request can legitimately see nothing yet. Waiting slightly longer than the interval is what
    /// makes the assertion above meaningful rather than vacuously passing on an empty table — which
    /// is also why the tests assert the table is non-empty where they can.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));
}
