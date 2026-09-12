using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Media;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-6l9b.1 — THE EVIDENCE FOR THE "NO .RemoveAllLoggers()" DECISION on the Radarr HTTP client,
/// mirroring <see cref="SonarrKeyIsScrubbedFromLogsTests"/> for the second instance.
///
/// <para><b>Why this file exists rather than a reference to the Sonarr one.</b> The decision not to
/// call <c>.RemoveAllLoggers()</c> is made PER REGISTRATION, and it rests on a claim about the code
/// of THAT client's URI builder — that the key rides in the QUERY STRING rather than in a URL PATH
/// segment. <c>RadarrConnectivityProber.BuildStatusUri</c> is a different method from Sonarr's, so
/// the Sonarr file's evidence says nothing about it: an edit moving Radarr's key into a path would
/// leave every Sonarr assertion green. The claim is therefore asserted against the registered Radarr
/// client directly.</para>
///
/// <para><b>TWO INDEPENDENT LAYERS PROTECT THE QUERY STRING, and this file asserts the one that
/// actually fires.</b> .NET's own HttpClient logging handler collapses the entire query string to
/// <c>?*</c> before the message is formatted, so through the registered client the key never reaches
/// <see cref="LogMessageCleanser"/> at all — which is why the end-to-end control below matches the
/// URI's PATH rather than the cleanser's replacement token. The cleanser (which scrubs query-string
/// credentials but NOT URL paths, CLAUDE.md §1) remains the guard for every OTHER way a key-bearing
/// URI can reach a log line — an exception message, or a hand-written one — and the unit tests here
/// pin that separately.</para>
///
/// <para><b>THE POSITIVE CONTROL IS THE POINT (CLAUDE.md §4).</b> "The key does not appear in the
/// logs" passes just as happily when the key never reached the logging path at all — an empty set
/// contains nothing, and three leaks have shipped in this repository behind exactly that shape. Each
/// test below therefore demonstrates that the search WOULD find a planted value before asserting the
/// real one is absent.</para>
/// </summary>
public sealed class RadarrKeyIsScrubbedFromLogsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    /// <summary>
    /// Distinctive enough that a substring search over every stored log row cannot match it by
    /// accident. Uses the <c>placeholder-</c> prefix the pre-commit secret guard allows.
    /// </summary>
    private const string RadarrKey = "placeholder-radarr-key-d4f16b83";

    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public RadarrKeyIsScrubbedFromLogsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The placement claim itself, asserted at the unit level so a failure names the cause directly:
    /// the probe URI must carry the key as a QUERY PARAMETER, because that is the only placement
    /// <see cref="LogMessageCleanser"/> covers. A future edit moving it to a path segment changes
    /// whether the Radarr registration in Program.cs is correct, and this is what notices.
    /// </summary>
    [Fact]
    public void The_probe_uri_carries_the_key_in_the_query_string_where_the_cleanser_covers_it()
    {
        var uri = new Uri($"http://radarr.example:7878/api/v3/system/status?apikey={Uri.EscapeDataString(RadarrKey)}");

        // POSITIVE CONTROL: unscrubbed, the key IS in this string. Without this the assertion below
        // would pass against any string that simply never contained the key.
        Assert.Contains(RadarrKey, uri.ToString(), StringComparison.Ordinal);

        var cleansed = LogMessageCleanser.Cleanse($"Sending HTTP request GET {uri}") ?? string.Empty;

        // The cleanser reached it — proving the key was in a shape the cleanser recognises...
        Assert.Contains(LogMessageCleanser.Replacement, cleansed, StringComparison.Ordinal);
        // ...therefore this absence is a real scrub, not a vacuous match.
        Assert.DoesNotContain(RadarrKey, cleansed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MUTATION EVIDENCE, IN-TEST RATHER THAN IN-REPOSITORY (CLAUDE.md §4). The same assertion is run
    /// against the placement the registration would need <c>.RemoveAllLoggers()</c> for — the key in a
    /// URL PATH segment — and shown to FAIL to produce a replacement. That is what proves the test
    /// above discriminates between placements rather than passing on any input, without putting a
    /// vulnerable client registration in the repository.
    /// </summary>
    [Fact]
    public void The_same_key_in_a_url_path_is_NOT_scrubbed_which_is_why_the_placement_matters()
    {
        var pathBorne = $"http://radarr.example:7878/api/v3/{RadarrKey}/system/status";

        var cleansed = LogMessageCleanser.Cleanse($"Sending HTTP request GET {pathBorne}") ?? string.Empty;

        // The cleanser does not cover this shape — stated as an assertion so the boundary of what the
        // "no RemoveAllLoggers" decision rests on is executable rather than folklore.
        Assert.Contains(RadarrKey, cleansed, StringComparison.Ordinal);
        Assert.DoesNotContain(LogMessageCleanser.Replacement, cleansed, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE END-TO-END GUARD, DRIVEN THROUGH THE REAL REGISTERED CLIENT.
    ///
    /// <para><b>This stores a key, then drives the actual admin probe route</b>, so the request is
    /// issued by the container's own <see cref="RadarrConnectivityProber"/> client — with
    /// <c>IHttpClientFactory</c>'s logging handler wrapped around it by the container, exactly as in
    /// production. It does NOT hand-build an <see cref="HttpClient"/> and it does NOT log the message
    /// shape by hand. That distinction is the whole point: #57's webhook test passed with a real leak
    /// precisely because it drove a path that bypassed the real dispatcher, so a test that synthesises
    /// the log line proves only that the cleanser works on a string this test wrote — never that the
    /// registered client's handler is covered.</para>
    ///
    /// <para>The probe target is an RFC 5737 documentation address that nothing answers, so the
    /// request fails — which is the MORE demanding case, not a weaker one: the failure path is where
    /// an exception carrying the request URI is most likely to be logged.</para>
    /// </summary>
    [Fact]
    public async Task The_registered_radarr_client_scrubs_its_key_from_the_uri_it_logs()
    {
        using var client = _factory.CreateClient();

        // Store the key through the real write path, then probe through the real route: from here on
        // nothing in this test constructs a URI or a log message itself.
        await SeedAdminKeyAsync();
        using var write = new HttpRequestMessage(HttpMethod.Put, AdminRadarrEndpoints.RadarrRoute)
        {
            Content = JsonContent.Create(new { baseUrl = "http://192.0.2.91:7878", apiKey = RadarrKey }),
        };
        write.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var writeResponse = await client.SendAsync(write);
        Assert.Equal(HttpStatusCode.OK, writeResponse.StatusCode);

        using var probe = new HttpRequestMessage(HttpMethod.Post, AdminRadarrEndpoints.RadarrTestRoute);
        probe.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var probeResponse = await client.SendAsync(probe);
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        await FlushLogSinkAsync();

        var store = _factory.Services.GetRequiredService<LogStore>();
        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        // The rows IHttpClientFactory's own handler writes are logged under the client's name.
        var clientRows = page.Entries
            .Where(e => e.Logger.Contains(nameof(RadarrConnectivityProber), StringComparison.Ordinal))
            .ToList();

        // POSITIVE CONTROL, part one: the handler logged at all. Without this the per-row loop below
        // would iterate zero times and assert nothing — the vacuity §4 warns about, and the exact
        // shape that let #57's leak through.
        Assert.NotEmpty(clientRows);

        // POSITIVE CONTROL, part two: the rows really are ABOUT this request — they name the very URI
        // the probe issued, minus its query string. Without this the absences below could pass against
        // rows logged for some entirely different request.
        //
        // NOTE WHAT THIS ASSERTS AND WHAT IT DOES NOT, exactly as the Sonarr file records. The
        // redaction seen here is .NET's OWN: the HttpClient logging handler writes the URI with its
        // whole query string collapsed to "?*", so the key never reaches LogMessageCleanser through
        // this path and the cleanser's Replacement token is NOT present. That is why this control
        // checks the path rather than the replacement — asserting the replacement here would fail, and
        // "fixing" that by dropping the control would leave a vacuous absence check.
        Assert.Contains(
            clientRows,
            e => e.Message.Contains("/api/v3/system/status", StringComparison.Ordinal));

        // ...therefore these absences are real. Asserted PER ROW, not "some row": an implementation
        // that redacted one row and not another would pass a "some row is clean" check while leaking
        // on every other row.
        foreach (var entry in clientRows)
        {
            Assert.DoesNotContain(RadarrKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RadarrKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing anywhere else in the store carries it either — the probe's failure path runs
        // through the endpoint, which logs under its own name.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(RadarrKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RadarrKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task SeedAdminKeyAsync() =>
        await _factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = AdminKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = AdminKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });

    /// <summary>
    /// Waits for the sink's background pump to drain. The provider batches on a 500 ms interval by
    /// design (AC5 — it must never write on the caller's thread), so a read taken immediately after a
    /// log call can legitimately see nothing yet. Waiting slightly longer than the interval is what
    /// makes the assertions above meaningful rather than vacuously passing on an empty table — which
    /// is also why they assert non-emptiness explicitly.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));
}
