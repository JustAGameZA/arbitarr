using Arbitarr.Api.Admin;
using Arbitarr.Core.Media;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Arbitarr.Data.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-6l9b.3 — THE EVIDENCE FOR THE "NO .RemoveAllLoggers()" DECISION on the two queue clients,
/// modelled on <see cref="SonarrKeyIsScrubbedFromLogsTests"/> and asserting the same two layers.
///
/// <para><b>Why this has to exist for the queue clients specifically rather than being inherited
/// from the probe's test.</b> Whether a client needs <c>.RemoveAllLoggers()</c> is a property of
/// THAT CLIENT'S URI BUILDER, not of the codebase: <c>IHttpClientFactory</c> attaches its logging
/// handler to every named client and writes the request URI at Information, which since #65 lands in
/// the persistent store served at <c>GET /api/admin/logs</c>. The probe's registrations are safe
/// because <c>SonarrConnectivityProber.BuildStatusUri</c> puts the key in the query string. These
/// registrations rest on the same claim about a DIFFERENT method —
/// <c>ArrQueueReader.BuildQueueUri</c> — which a future edit could falsify without touching the
/// prober at all, so it is asserted here rather than assumed to carry over.</para>
///
/// <para><b>THE POSITIVE CONTROL IS THE POINT (CLAUDE.md §4).</b> "The key does not appear in the
/// logs" passes just as happily when the key never reached the logging path at all — an empty set
/// contains nothing, and three leaks have shipped in this repository behind exactly that shape. Each
/// test below therefore first asserts that the handler LOGGED (rows exist under the client's own
/// category) and that those rows are ABOUT THIS REQUEST (they name the queue path), before asserting
/// any absence.</para>
///
/// <para><b>NOTE WHICH LAYER FIRES HERE.</b> The redaction seen through the registered client is
/// .NET's OWN: the logging handler collapses the whole query string to <c>?*</c> before the message
/// is formatted, so the key never reaches <see cref="LogMessageCleanser"/> through this path and the
/// cleanser's <c>Replacement</c> token is NOT present. That is why the control below asserts the
/// URI's PATH and the <c>?*</c> collapse rather than the replacement — asserting the replacement here
/// would fail, and "fixing" that by dropping the control would leave a vacuous absence check. The
/// cleanser remains the guard for every OTHER way a key-bearing URI can reach a log line, which
/// <see cref="SonarrKeyIsScrubbedFromLogsTests"/> pins directly.</para>
/// </summary>
public sealed class ArrQueueKeyIsScrubbedFromLogsTests
{
    private const string AdminKey = "the-real-admin-key";

    /// <summary>
    /// Distinctive enough that a substring search over every stored log row cannot match it by
    /// accident. Uses the <c>placeholder-</c> prefix the pre-commit secret guard allows.
    /// </summary>
    private const string SonarrKey = "placeholder-sonarr-key-c93f1a05";

    private const string RadarrKey = "placeholder-radarr-key-d04g2b16";

    /// <summary>
    /// THE SONARR QUEUE CLIENT, DRIVEN THROUGH ITS REAL ROUTE.
    ///
    /// <para>This stores a key through the real write path, then drives the actual admin queue route,
    /// so the request is issued by the container's own <see cref="SonarrQueueClient"/> — with
    /// <c>IHttpClientFactory</c>'s logging handler wrapped around it by the container, exactly as in
    /// production. It does NOT hand-build an <see cref="System.Net.Http.HttpClient"/> and it does NOT
    /// log the message shape by hand. That distinction is the whole point: #57's webhook test passed
    /// with a real leak precisely because it drove a path that bypassed the real dispatcher.</para>
    ///
    /// <para>The target is a documentation address that nothing answers on, so the request fails —
    /// which is the MORE demanding case, not a weaker one: the failure path is where an exception
    /// carrying the request URI is most likely to be logged.</para>
    /// </summary>
    [Fact]
    public async Task The_registered_sonarr_queue_client_scrubs_its_key_from_the_uri_it_logs()
    {
        using var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        await factory.SeedAsync(async db =>
            await new ArrInstanceRepository(db).SetAsync(
                "http://sonarr.example:8989", SonarrKey, CancellationToken.None));

        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, AdminArrQueueEndpoints.SonarrQueueRoute);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        await AssertKeyIsAbsentFromEveryLogRowAsync(factory, nameof(SonarrQueueClient), SonarrKey);
    }

    /// <summary>The same, for the Radarr queue client's own registration and logger category.</summary>
    [Fact]
    public async Task The_registered_radarr_queue_client_scrubs_its_key_from_the_uri_it_logs()
    {
        using var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        await factory.SeedAsync(async db =>
            await new RadarrInstanceRepository(db).SetAsync(
                "http://radarr.example:7878", RadarrKey, CancellationToken.None));

        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, AdminArrQueueEndpoints.RadarrQueueRoute);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        await AssertKeyIsAbsentFromEveryLogRowAsync(factory, nameof(RadarrQueueClient), RadarrKey);
    }

    /// <summary>
    /// The shared assertion: the client logged, the rows are about this request, the <c>?*</c>
    /// collapse fired, and the key is absent from EVERY row — asserted per row rather than "some row
    /// is clean", because an implementation that redacted one row and not another would pass the
    /// weaker check while leaking on every other one.
    /// </summary>
    private static async Task AssertKeyIsAbsentFromEveryLogRowAsync(
        ArbitarrWebApplicationFactory factory,
        string clientCategory,
        string apiKey)
    {
        await FlushLogSinkAsync();

        var store = factory.Services.GetRequiredService<LogStore>();
        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        // The rows IHttpClientFactory's own handler writes are logged under the client's name, which
        // is why the two queue clients are separate types rather than one shared instance.
        var clientRows = page.Entries
            .Where(e => e.Logger.Contains(clientCategory, StringComparison.Ordinal))
            .ToList();

        // POSITIVE CONTROL, part one: the handler logged at all. Without this the per-row loop below
        // would iterate zero times and assert nothing — the vacuity §4 warns about, and the exact
        // shape that let #57's leak through.
        Assert.NotEmpty(clientRows);

        // POSITIVE CONTROL, part two: the rows really are ABOUT this request — they name the very
        // URI the read issued, minus its query string. Without this the absence below could pass
        // against rows logged for some entirely different request.
        Assert.Contains(clientRows, e => e.Message.Contains("/api/v3/queue", StringComparison.Ordinal));

        // POSITIVE CONTROL, part three: the ?* COLLAPSE ACTUALLY FIRED. This is the layer the "no
        // RemoveAllLoggers" decision rests on, so it is asserted to have HAPPENED rather than
        // inferred from the key's absence — a URI logged with no query string at all would also show
        // no key, and would mean the collapse was never exercised.
        Assert.Contains(clientRows, e => e.Message.Contains("?*", StringComparison.Ordinal));

        // ...therefore these absences are real. PER ROW.
        foreach (var entry in clientRows)
        {
            Assert.DoesNotContain(apiKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(apiKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing anywhere else in the store carries it either — the failure path runs through
        // the reader and the endpoint, which log under their own names.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(apiKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(apiKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task SeedAdminKeyAsync(ArbitarrWebApplicationFactory factory) =>
        await factory.SeedAsync(async db =>
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
    /// design (it must never write on the caller's thread), so a read taken immediately after a log
    /// call can legitimately see nothing yet. Waiting slightly longer than the interval is what makes
    /// the assertions above meaningful rather than vacuously passing on an empty table — which is
    /// also why they assert non-emptiness explicitly.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));
}
