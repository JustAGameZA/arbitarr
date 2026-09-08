using Arbitarr.Core.Sources;
using Arbitarr.Data.Logging;
using Arbitarr.Sources.NzbHydra;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #99 / CLAUDE.md §1 — the UPSTREAM source key must never reach the persistent log store.
///
/// <para>
/// This is the sibling of <see cref="LogSecretInjectionTests"/>, which guards the INBOUND client
/// key. The upstream key is the other half and travels a different path: <c>IHttpClientFactory</c>
/// attaches its own logging handler to every named/typed client — <c>NzbHydraSource</c> is
/// registered with <c>AddHttpClient&lt;NzbHydraSource&gt;()</c> — and that handler logs the FULL
/// ABSOLUTE URI at Information, which since #65 lands in the SQLite log store served at
/// <c>/api/admin/logs</c>. Both redaction layers in front of that store key on QUERY-STRING shape
/// (see the measured note below on which one actually fires), so the key is covered exactly as long
/// as it stays in the query string.
/// </para>
///
/// <para>
/// That is the constraint #99 could plausibly have broken. #99 changed how the upstream URL is
/// built (the path is now chosen per protocol), and moving the key into the PATH while doing so
/// would have left it outside every cleanser pattern and logged verbatim — a silent, permanent
/// credential leak into a store the admin UI renders. This test pins it.
/// </para>
///
/// <para>
/// <b>Which mechanism actually redacts this, measured rather than assumed.</b> The first version of
/// this test asserted <see cref="LogMessageCleanser.Replacement"/> appeared in the logged URI, on
/// the assumption that the cleanser is what scrubs it. It is not — the test failed, and the logged
/// rows showed why: <c>HttpClient</c>'s own logging handler redacts the query string BEFORE the
/// message ever reaches Arbitarr's sink, writing <c>GET http://…/torznab/api?*</c>. The whole query
/// string is replaced by a literal <c>?*</c>, so the cleanser has nothing left to match and its
/// marker never appears. The key is protected by .NET's redaction, with
/// <see cref="LogMessageCleanser"/> as the second layer behind it for any OTHER call site that
/// logs a URI itself.
/// </para>
///
/// <para>
/// That distinction is the whole reason the apikey must stay in the QUERY STRING (CLAUDE.md §1).
/// Both layers key on query-string shape: .NET redacts the query and nothing else, and the
/// cleanser's patterns match <c>?key=</c>/<c>&amp;key=</c> forms. A key moved into the URL PATH
/// would be outside both and logged verbatim, forever, into a store the admin UI renders.
/// </para>
///
/// <para>
/// <b>Non-vacuity.</b> An <c>Assert.DoesNotContain(key, row)</c> sweep passes just as happily when
/// the key never reached the logger at all, which would make this a no-op the day the handler stops
/// logging. So the assertion is two-sided, following <see cref="LogSecretInjectionTests"/>'s
/// reference shape: the test first proves the upstream request WAS logged, that the logged row is
/// the one carrying our URI, and that the query string was positively redacted (the <c>?*</c>
/// marker is present where the key would otherwise be) — and only then asserts no row carries the
/// key. The positive control demonstrates the URI reached the logging path and was scrubbed there,
/// rather than never arriving.
/// </para>
/// </summary>
public sealed class UpstreamApiKeyLogRedactionTests : IClassFixture<WebApplicationFactory<Program>>
{
    // Distinctive enough to find in a log row, and prefixed with the value the pre-commit secret
    // guard allowlists (the convention LogSecretInjectionTests already follows).
    private const string UpstreamApiKey = "secret-api-key-upstream-99-probe";

    // TEST-NET-1 (RFC 5737, documentation-only) on a port nothing listens on: the request fails at
    // connect, which is what makes the factory's handler log the request URI and then the failure.
    // Never a real host or an RFC1918 address.
    private const string UnreachableUpstream = "http://192.0.2.99:5076/";

    private readonly WebApplicationFactory<Program> _factory;

    public UpstreamApiKeyLogRedactionTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-upstream-key-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);

        _factory = factory.WithWebHostBuilder(
            builder => builder.UseSetting("Arbitarr:ConfigDir", configDirectory));
    }

    [Theory]
    [InlineData(SearchProtocol.Torznab)]
    [InlineData(SearchProtocol.Newznab)]
    public async Task The_upstream_api_key_is_redacted_from_every_log_row_on_both_endpoints(
        SearchProtocol protocol)
    {
        // A real NzbHydraSource over the REAL typed HttpClient, so the factory's logging handler is
        // in the pipeline exactly as it is in production. Pointed at an unreachable address: the
        // resulting failure is logged with the request URI attached, which is the leak path.
        using var scope = _factory.Services.CreateScope();
        var httpClient = scope.ServiceProvider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(NzbHydraSource));

        var source = new NzbHydraSource(
            new NzbHydraSourceOptions(
                new Uri(UnreachableUpstream),
                UpstreamApiKey,
                "upstream-key-probe-source",
                RequestTimeout: TimeSpan.FromSeconds(2)),
            httpClient,
            scope.ServiceProvider.GetRequiredService<Arbitarr.Core.Sources.CircuitBreaker.IAsyncCircuitBreaker>());

        // The call is expected to fail (nothing is listening); the logging is the point, not the
        // result. Swallow only the connection failure so a different exception still fails loudly.
        try
        {
            await source.SearchAsync(new SearchQuery("upstream-key-probe", Array.Empty<int>(), 5, protocol));
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        var store = _factory.Services.GetRequiredService<LogStore>();
        await FlushLogSinkAsync();

        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        // POSITIVE CONTROL, part 1: something was logged at all.
        Assert.NotEmpty(page.Entries);

        // POSITIVE CONTROL, part 2 — the one that makes the sweep below bite. Find the rows that
        // logged OUR upstream request and prove the request URI genuinely passed through the
        // logging path and was redacted there. Without this the sweep would pass on a run where the
        // handler logged nothing about this request at all, and the test would be vacuous.
        var expectedPath = protocol == SearchProtocol.Torznab ? "/torznab/api" : "/api";
        var upstreamRows = page.Entries
            .Where(e => e.Message.Contains("192.0.2.99:5076" + expectedPath, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(upstreamRows);

        // The redaction marker itself: HttpClient's logging handler replaces the entire query
        // string with "?*". Its presence proves a URI that HAD a query string (the one carrying the
        // apikey) reached the logger and was scrubbed, rather than the key simply never being sent.
        Assert.All(upstreamRows, e => Assert.Contains("?*", e.Message, StringComparison.Ordinal));

        // The actual assertion: across EVERY field of EVERY row, the key is gone. Checking only
        // Message would miss the exception text, which is the field most likely to drag a request
        // URI along.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(UpstreamApiKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(UpstreamApiKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(UpstreamApiKey, entry.Logger, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Waits for the sink's background pump to drain; see
    /// <see cref="LogSecretInjectionTests"/>'s own note on why this is what keeps the assertions
    /// above meaningful rather than vacuously true of an empty table.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));
}
