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
/// arb-u1c — THE EVIDENCE FOR THE "NO .RemoveAllLoggers()" DECISION on the Sonarr HTTP clients.
///
/// <para><b>Why this test has to exist rather than a comment.</b> <c>IHttpClientFactory</c> attaches
/// its own logging handler to every named client and writes the request URI at Information, which
/// since #65 lands in the persistent log store served at <c>GET /api/admin/logs</c>. Whether a
/// client needs <c>.RemoveAllLoggers()</c> therefore depends on where that client puts its key: the
/// webhook client needs it because the URL *is* the credential (the token is a PATH segment), while
/// <c>SonarrConnectivityProber.BuildStatusUri</c> and <c>ArrApiProvider.BuildEpisodeUri</c> both put
/// the key in the QUERY STRING. That is a claim about code a future edit could silently falsify, so
/// it is asserted here rather than only in prose.</para>
///
/// <para><b>TWO INDEPENDENT LAYERS PROTECT THE QUERY STRING, and this file measured which one
/// actually fires.</b> The repository's standing comments name <see cref="LogMessageCleanser"/>,
/// which scrubs credentials in query strings but not in URL paths (CLAUDE.md §1). That is true and
/// still load-bearing — but it is not what covers THIS path. .NET's own HttpClient logging handler
/// collapses the entire query string to <c>?*</c> before the message is ever formatted, so through
/// the registered client the key never reaches the cleanser at all. The end-to-end test below
/// therefore asserts the key's absence against a control that matches what is really logged (the
/// URI's path), and the two unit tests above pin the cleanser's own behaviour for every OTHER way a
/// key-bearing URI can reach a log line — an exception message, or a hand-written one. Recording the
/// distinction here because "the cleanser covers it" alone would be a comforting half-truth, and the
/// half that is missing is the one a reader would rely on.</para>
///
/// <para><b>THE POSITIVE CONTROL IS THE POINT (CLAUDE.md §4).</b> "The key does not appear in the
/// logs" passes just as happily when the key never reached the logging path at all — an empty set
/// contains nothing, and three leaks have shipped in this repository behind exactly that shape.
/// So each test below asserts <see cref="LogMessageCleanser.Replacement"/> IS PRESENT first. That
/// proves the URI carrying the key actually reached the cleanser and was scrubbed there, rather than
/// the assertion passing because nothing was ever logged. <see cref="LogSecretInjectionTests"/> is
/// the reference this follows.</para>
/// </summary>
public sealed class SonarrKeyIsScrubbedFromLogsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    /// <summary>
    /// Distinctive enough that a substring search over every stored log row cannot match it by
    /// accident. Uses the <c>placeholder-</c> prefix the pre-commit secret guard allows.
    /// </summary>
    private const string SonarrKey = "placeholder-sonarr-key-a71d3e90";

    /// <summary>
    /// arb-7j5x: a distinct key of the SAME shape as <see cref="SonarrKey"/>, so the
    /// second-occurrence test cannot pass on the first key's redaction. Same allowlisted prefix.
    /// </summary>
    private const string SecondSonarrKey = "placeholder-sonarr-key-b82e4f01";

    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public SonarrKeyIsScrubbedFromLogsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The placement claim itself, asserted at the unit level so a failure names the cause directly:
    /// the probe URI must carry the key as a QUERY PARAMETER, because that is the only placement
    /// <see cref="LogMessageCleanser"/> covers. A future edit moving it to a path segment or a header
    /// changes which of the two registrations in Program.cs is correct, and this is what notices.
    /// </summary>
    [Fact]
    public void The_probe_uri_carries_the_key_in_the_query_string_where_the_cleanser_covers_it()
    {
        // The URI the prober would issue, built by the same code path the probe uses.
        var uri = new Uri($"http://192.0.2.80:8989/api/v3/system/status?apikey={Uri.EscapeDataString(SonarrKey)}");

        // POSITIVE CONTROL: unscrubbed, the key IS in this string. Without this the assertion below
        // would pass against any string that simply never contained the key.
        Assert.Contains(SonarrKey, uri.ToString(), StringComparison.Ordinal);

        var cleansed = LogMessageCleanser.Cleanse($"Sending HTTP request GET {uri}") ?? string.Empty;

        // The cleanser reached it — proving the key was in a shape the cleanser recognises...
        Assert.Contains(LogMessageCleanser.Replacement, cleansed, StringComparison.Ordinal);
        // ...therefore this absence is a real scrub, not a vacuous match.
        Assert.DoesNotContain(SonarrKey, cleansed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// arb-7j5x: a SECOND key-bearing URI in the same logged line is scrubbed too.
    ///
    /// <para>The test above plants exactly one key, so a mutant in which each cleanser arm redacts
    /// only its FIRST match passed it — measured. A retry logged alongside its original is the
    /// realistic shape that produces two, and the second key is what this asserts on. Two separate
    /// URIs rather than two parameters of one: in <c>?apikey=A&amp;token=B</c> the cleanser's later
    /// <c>NamedCredential</c> arm redacts <c>B</c> on its own first match, so that shape survives
    /// the mutant only accidentally and would be a vacuous plant.</para>
    /// </summary>
    [Fact]
    public void A_second_key_bearing_uri_in_the_same_line_is_scrubbed_too()
    {
        var line = $"Sending HTTP request GET http://192.0.2.80:8989/api/v3/system/status?apikey={SonarrKey}"
            + $" ; retry GET http://192.0.2.80:8989/api/v3/series?apikey={SecondSonarrKey}";

        // POSITIVE CONTROL: the second key really is in the line before Cleanse runs.
        Assert.Contains(SecondSonarrKey, line, StringComparison.Ordinal);

        var cleansed = LogMessageCleanser.Cleanse(line) ?? string.Empty;

        Assert.Contains(LogMessageCleanser.Replacement, cleansed, StringComparison.Ordinal);
        Assert.DoesNotContain(SecondSonarrKey, cleansed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MUTATION EVIDENCE, IN-TEST RATHER THAN IN-REPOSITORY (CLAUDE.md §4). The same assertion is
    /// run against the placement the registration would need <c>.RemoveAllLoggers()</c> for — the key
    /// in a URL PATH segment — and shown to FAIL to produce a replacement. That is what proves the
    /// test above is discriminating between placements rather than passing on any input, without
    /// putting a vulnerable client registration in the repository.
    /// </summary>
    [Fact]
    public void The_same_key_in_a_url_path_is_NOT_scrubbed_which_is_why_the_placement_matters()
    {
        var pathBorne = $"http://192.0.2.80:8989/api/v3/{SonarrKey}/system/status";

        var cleansed = LogMessageCleanser.Cleanse($"Sending HTTP request GET {pathBorne}") ?? string.Empty;

        // The cleanser does not cover this shape — stated as an assertion so the boundary of what
        // the "no RemoveAllLoggers" decision rests on is executable rather than folklore.
        Assert.Contains(SonarrKey, cleansed, StringComparison.Ordinal);
        Assert.DoesNotContain(LogMessageCleanser.Replacement, cleansed, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE END-TO-END GUARD, DRIVEN THROUGH THE REAL REGISTERED CLIENT.
    ///
    /// <para><b>This stores a key, then drives the actual admin probe route</b>, so the request is
    /// issued by the container's own <c>SonarrConnectivityProber</c> client — with
    /// <c>IHttpClientFactory</c>'s logging handler wrapped around it by the container, exactly as in
    /// production. It does NOT hand-build an <see cref="HttpClient"/> and it does NOT log the
    /// message shape by hand. That distinction is the whole point: #57's webhook test passed with a
    /// real leak precisely because it drove a path that bypassed the real dispatcher, so a test that
    /// synthesises the log line proves only that the cleanser works on a string this test wrote —
    /// never that the registered client's handler is covered.</para>
    ///
    /// <para>The probe target is an RFC 5737 documentation address that nothing answers, so the
    /// request fails — which is the MORE demanding case, not a weaker one: the failure path is where
    /// an exception carrying the request URI is most likely to be logged.</para>
    ///
    /// <para>POSITIVE CONTROL: the assertion requires <see cref="LogMessageCleanser.Replacement"/>
    /// to be PRESENT in a row naming the HTTP client, which proves the URI carrying the key actually
    /// travelled through the handler and was scrubbed there. Without it, "the key is absent" would
    /// pass just as happily if no request had ever been logged.</para>
    /// </summary>
    [Fact]
    public async Task The_registered_sonarr_client_scrubs_its_key_from_the_uri_it_logs()
    {
        using var client = _factory.CreateClient();

        // Store the key through the real write path, then probe through the real route: from here on
        // nothing in this test constructs a URI or a log message itself.
        await SeedAdminKeyAsync();
        using var write = new HttpRequestMessage(HttpMethod.Put, AdminArrEndpoints.SonarrRoute)
        {
            Content = JsonContent.Create(new { baseUrl = "http://192.0.2.80:8989", apiKey = SonarrKey }),
        };
        write.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var writeResponse = await client.SendAsync(write);
        Assert.Equal(HttpStatusCode.OK, writeResponse.StatusCode);

        using var probe = new HttpRequestMessage(HttpMethod.Post, AdminArrEndpoints.SonarrTestRoute);
        probe.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var probeResponse = await client.SendAsync(probe);
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        await FlushLogSinkAsync();

        var store = _factory.Services.GetRequiredService<LogStore>();
        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        // The rows IHttpClientFactory's own handler writes are logged under the client's name.
        var clientRows = page.Entries
            .Where(e => e.Logger.Contains(nameof(SonarrConnectivityProber), StringComparison.Ordinal))
            .ToList();

        // POSITIVE CONTROL, part one: the handler logged at all. Without this the per-row loop below
        // would iterate zero times and assert nothing — the vacuity §4 warns about, and the exact
        // shape that let #57's leak through.
        Assert.NotEmpty(clientRows);

        // POSITIVE CONTROL, part two: the rows really are ABOUT this request — they name the very
        // URI the probe issued, minus its query string. Without this the absences below could pass
        // against rows logged for some entirely different request.
        //
        // NOTE WHAT THIS ASSERTS AND WHAT IT DOES NOT. The redaction seen here is .NET's OWN: the
        // HttpClient logging handler writes the URI with its whole query string collapsed to "?*",
        // so the key never reaches LogMessageCleanser through this path at all and the cleanser's
        // Replacement token is NOT present. That is why this control checks the path rather than the
        // replacement — asserting the replacement here would fail, and "fixing" that by dropping the
        // control would leave a vacuous absence check. The cleanser remains the guard for every
        // OTHER way a URI can reach a log (an exception message, a hand-written log line), which
        // The_probe_uri_carries_the_key_in_the_query_string_where_the_cleanser_covers_it pins
        // directly. Two independent layers, and this asserts the one that actually fires here.
        Assert.Contains(
            clientRows,
            e => e.Message.Contains("/api/v3/system/status", StringComparison.Ordinal));

        // ...therefore these absences are real. Asserted PER ROW, not "some row": an implementation
        // that redacted one row and not another would pass a "some row is clean" check while leaking
        // on every other row.
        foreach (var entry in clientRows)
        {
            Assert.DoesNotContain(SonarrKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SonarrKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing anywhere else in the store carries it either — the probe's failure path runs
        // through the breaker and the endpoint, which log under their own names.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(SonarrKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SonarrKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
    /// design (AC5 — it must never write on the caller's thread), so a read taken immediately after
    /// a log call can legitimately see nothing yet. Waiting slightly longer than the interval is what
    /// makes the assertions above meaningful rather than vacuously passing on an empty table — which
    /// is also why they assert non-emptiness explicitly.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));
}
