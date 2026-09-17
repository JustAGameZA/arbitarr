using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Arbitarr.Data.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-7rr8 — THE EVIDENCE FOR THE SOURCE PROBER, modelled on
/// <see cref="ArrQueueKeyIsScrubbedFromLogsTests"/> and <see cref="SonarrKeyIsScrubbedFromLogsTests"/>.
///
/// <para><b>Why this has to exist for the source prober specifically.</b> Whether a client needs
/// <c>.RemoveAllLoggers()</c> is a property of THAT CLIENT'S URI builder, not of the codebase:
/// <c>IHttpClientFactory</c> attaches its own logging handler to every named client and writes the
/// request URI at Information, which since #65 lands in the persistent store served at
/// <c>GET /api/admin/logs</c>. <c>SourceConnectivityProber.BuildCapsUri</c>'s own doc comment
/// asserted only that the URI "is never logged or returned" — true of this type's own code, but silent
/// on what the registered <c>IHttpClientFactory</c> client built around it in Program.cs actually does.
/// That gap is what this file closes: it drives the REGISTERED client, not a hand-built one, through
/// the real <c>POST /api/admin/sources/{id}/test</c> route.</para>
///
/// <para><b>NOTE WHICH LAYER FIRES HERE.</b> The redaction seen through the registered client is .NET's
/// OWN: the logging handler collapses the whole query string to <c>?*</c> before the message is
/// formatted, so the key never reaches <see cref="LogMessageCleanser"/> through this path and the
/// cleanser's <c>Replacement</c> token is NOT present. That is why the control below asserts the URI's
/// PATH and the <c>?*</c> collapse rather than the replacement — asserting the replacement here would
/// fail, and "fixing" that by dropping the control would leave a vacuous absence check. The cleanser
/// remains the guard for every OTHER way a key-bearing URI can reach a log line, which
/// <see cref="SonarrKeyIsScrubbedFromLogsTests"/> pins directly for the same claim on a sibling client.</para>
///
/// <para><b>THE POSITIVE CONTROL IS THE POINT (CLAUDE.md §4).</b> "The key does not appear in the logs"
/// passes just as happily when the key never reached the logging path at all — an empty set contains
/// nothing, and three leaks have shipped in this repository behind exactly that shape. This test
/// therefore first asserts that the handler LOGGED (rows exist under the client's own category) and
/// that those rows are ABOUT THIS REQUEST (they name the caps path) and that the <c>?*</c> collapse
/// actually fired, before asserting any absence — per row, not "some row".</para>
/// </summary>
public sealed class SourceProberKeyIsScrubbedFromLogsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    /// <summary>
    /// Distinctive enough that a substring search over every stored log row cannot match it by
    /// accident. Uses the <c>placeholder-</c> prefix the pre-commit secret guard allows.
    /// </summary>
    private const string SourceKey = "placeholder-source-key-e15h3c27";

    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public SourceProberKeyIsScrubbedFromLogsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// THE END-TO-END GUARD, DRIVEN THROUGH THE REAL REGISTERED CLIENT.
    ///
    /// <para>This creates a source through the real write path (which stores the key write-only, per
    /// CLAUDE.md §1), then drives the actual admin test route, so the request is issued by the
    /// container's own <see cref="SourceConnectivityProber"/> client — with <c>IHttpClientFactory</c>'s
    /// logging handler wrapped around it by the container, exactly as in production. It does NOT
    /// hand-build an <see cref="HttpClient"/> and it does NOT log the message shape by hand. That
    /// distinction is the whole point: #57's webhook test passed with a real leak precisely because it
    /// drove a path that bypassed the real dispatcher.</para>
    ///
    /// <para>The source's base URL is an RFC 5737 documentation address that nothing answers, so the
    /// probe fails — which is the MORE demanding case, not a weaker one: the failure path is where an
    /// exception carrying the request URI is most likely to be logged.</para>
    /// </summary>
    [Fact]
    public async Task The_registered_source_prober_client_scrubs_its_key_from_the_uri_it_logs()
    {
        using var client = _factory.CreateClient();

        await SeedAdminKeyAsync();

        using var create = new HttpRequestMessage(HttpMethod.Post, AdminSourceEndpoints.SourcesRoute)
        {
            Content = JsonContent.Create(new
            {
                kind = SourceRepository.NzbHydraKind,
                displayName = "arb-7rr8-source-prober-test",
                baseUrl = "http://192.0.2.90:5076",
                apiKey = SourceKey,
                enabled = true,
            }),
        };
        create.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var createResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<CreatedSourceResponse>();
        Assert.NotNull(created);

        using var probe = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AdminSourceEndpoints.SourcesRoute}/{created!.Id}/test");
        probe.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var probeResponse = await client.SendAsync(probe);
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);

        await _factory.Services.FlushLogSinkAsync();

        var store = _factory.Services.GetRequiredService<LogStore>();
        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        // The rows IHttpClientFactory's own handler writes are logged under the client's name.
        var clientRows = page.Entries
            .Where(e => e.Logger.Contains(nameof(SourceConnectivityProber), StringComparison.Ordinal))
            .ToList();

        // POSITIVE CONTROL, part one: the handler logged at all. Without this the per-row loop below
        // would iterate zero times and assert nothing — the vacuity §4 warns about, and the exact shape
        // that let #57's leak through.
        Assert.NotEmpty(clientRows);

        // POSITIVE CONTROL, part two: the rows really are ABOUT this request — they name the caps path
        // the probe issued. Without this the absences below could pass against rows logged for some
        // entirely different request.
        Assert.Contains(clientRows, e => e.Message.Contains("/api", StringComparison.Ordinal));

        // POSITIVE CONTROL, part three: the ?* COLLAPSE ACTUALLY FIRED. This is the layer the doc
        // comment's corrected claim rests on, so it is asserted to have HAPPENED rather than inferred
        // from the key's absence — a URI logged with no query string at all would also show no key, and
        // would mean the collapse was never exercised.
        Assert.Contains(clientRows, e => e.Message.Contains("?*", StringComparison.Ordinal));

        // ...therefore these absences are real. Asserted PER ROW, not "some row": an implementation
        // that redacted one row and not another would pass a "some row is clean" check while leaking on
        // every other row.
        foreach (var entry in clientRows)
        {
            Assert.DoesNotContain(SourceKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SourceKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing anywhere else in the store carries it either — the probe's failure path runs
        // through the endpoint and the credential provider, which log under their own names.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(SourceKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SourceKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Only the id is needed from the create response to drive the follow-up test route.</summary>
    private sealed record CreatedSourceResponse(long Id);
}
