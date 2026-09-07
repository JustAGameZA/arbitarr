using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #57's notification configuration surface end to end against the real Host.
///
/// <para><b>The property this file exists to pin: a webhook URL never comes back out.</b> For a
/// notification webhook the URL <i>is</i> the credential — Discord, Telegram, Gotify and Notifiarr
/// all carry the token in the path — so it is stored write-only exactly as a source API key is (see
/// <see cref="AdminSourceEndpointsTests"/>, whose leak-assertion idiom this follows). The sweep in
/// <see cref="AdminApiKeyRouteEnumerationTests"/> already covers gating generically for these
/// routes because all four are concrete rather than <c>{id}</c>-templated; the per-route assertions
/// here pin it by name anyway so no notification route is gated only by assumption.</para>
///
/// <para>Every leak assertion here carries <b>both halves of a positive control</b>, and the
/// difference between them is the point. <b>Existence</b> — the URL really is stored, via the store
/// and via the surface's own <c>hasWebhookUrl</c> indicator — stops an assertion passing merely
/// because nothing was ever configured. <b>Detectability</b> — the search used to assert absence is
/// first shown to go RED against a body of the same shape that does carry the value — stops it
/// passing because the search itself was incapable of finding anything. CLAUDE.md §4 is explicit
/// that the first does not imply the second: "asserting the fixture was created proves the secret
/// exists. It does not prove it would be detectable if it leaked."</para>
///
/// <para>Two assertions in this file originally had existence controls only (the settings-catalog
/// sweep and the config-response sweep), which is the shape that let three earlier leaks through
/// review. Their detectability halves are built by <see cref="SerializeAsALeakWould"/> and
/// <see cref="ProjectSettingsTableAsALeakWouldAsync"/>, which construct the body the corresponding
/// regression WOULD have produced rather than planting a literal — so the control is a real
/// mutation of the projection under test, and no vulnerable code enters the product to provide it.
/// The event-row and log-row sweeps were already doubly controlled (a real
/// <c>NotificationDispatcher</c> cycle asserted non-empty; the log page asserted non-empty after a
/// flush) and are unchanged.</para>
///
/// <para>All URLs are obviously-fake <c>example.com</c> forms and all key material is
/// <c>placeholder-*</c>: no real endpoint or secret ever enters committed content.</para>
/// </summary>
public sealed class AdminNotificationEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string NotificationsRoute = "/api/admin/notifications";
    private const string WebhookRoute = "/api/admin/notifications/webhook";
    private const string TestRoute = "/api/admin/notifications/test";

    /// <summary>
    /// The value the leak assertions hunt for, shaped the way real providers shape one — token in
    /// the PATH. Distinctive on purpose: a substring search for it across a whole response body
    /// cannot collide with anything the payload legitimately contains, so a hit is unambiguously
    /// the secret escaping.
    /// </summary>
    private const string SecretWebhookUrl = "https://example.com/hooks/placeholder-super-secret-webhook-token";

    /// <summary>
    /// The token half of <see cref="SecretWebhookUrl"/>. Searched for separately so a change that
    /// logged only the path or the query — rather than the whole absolute URL — still fails.
    /// </summary>
    private const string WebhookTokenFragment = "placeholder-super-secret-webhook-token";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminNotificationEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", NotificationsRoute)]
    [InlineData("PUT", NotificationsRoute)]
    [InlineData("DELETE", WebhookRoute)]
    [InlineData("POST", TestRoute)]
    public async Task Every_notification_route_rejects_a_request_without_the_admin_key(string method, string path)
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        // No body, deliberately: a required body would be model-bound BEFORE the endpoint filter
        // and short-circuit to 400 without the gate ever running. These routes bind optionally, so
        // the gate stays strictly first and an unkeyed caller learns nothing.
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
            $"Expected {method} {path} to be admin-gated, but it returned {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task The_gate_is_by_path_prefix_rather_than_by_verb_so_the_read_is_gated_too()
    {
        // The read carries the notifier's configuration — thresholds and last-delivery state are
        // operator configuration, not dashboard facts — so GET is gated exactly like the writes.
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var unkeyed = await client.GetAsync(NotificationsRoute);
        Assert.True(unkeyed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable);

        using var keyed = await SendAsync(client, HttpMethod.Get, NotificationsRoute);
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
    }

    [Fact]
    public async Task A_fresh_install_reports_the_notifier_off_with_no_webhook_configured()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, NotificationsRoute);
        var config = await response.Content.ReadFromJsonAsync<NotificationConfigResponse>();

        Assert.NotNull(config);
        Assert.False(config!.HasWebhookUrl);
    }

    [Fact]
    public async Task Configuring_a_webhook_reports_presence_as_a_bool_and_never_echoes_the_url()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var put = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            enabled = true,
            webhookUrl = SecretWebhookUrl,
            consecutiveFailureThreshold = 3,
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await put.Content.ReadAsStringAsync();
        var config = System.Text.Json.JsonSerializer.Deserialize<NotificationConfigResponse>(
            body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        // NON-VACUOUS, HALF ONE — EXISTENCE: the URL really was accepted and stored, so the
        // response below is reporting on a configuration that actually holds one...
        Assert.NotNull(config);
        Assert.True(config!.HasWebhookUrl);

        // NON-VACUOUS, HALF TWO — DETECTABILITY: ...and the searches used below really would find
        // it. HasWebhookUrl == true proves the secret EXISTS; on its own it says nothing about
        // whether a leak into THIS body would be caught, which is the distinction CLAUDE.md §4
        // draws. Serializing the response record's leaky counterpart through the same serializer
        // gives the body a regression would have produced — a record that grew the nullable "url"
        // property NotificationConfigResponse's doc warns against — and proves both searches go red
        // against it.
        var bodyIfTheResponseCarriedTheUrl = SerializeAsALeakWould(config);

        Assert.Contains(SecretWebhookUrl, bodyIfTheResponseCarriedTheUrl, StringComparison.Ordinal);
        Assert.Contains(WebhookTokenFragment, bodyIfTheResponseCarriedTheUrl, StringComparison.Ordinal);

        // The real assertions, now known to be capable of failing.
        Assert.DoesNotContain(SecretWebhookUrl, body, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookTokenFragment, body, StringComparison.Ordinal);

        // The same on the read path, which is the projection an operator's browser actually loads.
        using var get = await SendAsync(client, HttpMethod.Get, NotificationsRoute);
        var readBody = await get.Content.ReadAsStringAsync();

        Assert.Contains("\"hasWebhookUrl\":true", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretWebhookUrl, readBody, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookTokenFragment, readBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The response body a LEAKY <see cref="NotificationConfigResponse"/> would have serialized —
    /// the mutation this test's control needs, expressed as a local shape rather than by putting a
    /// vulnerable record in the product (CLAUDE.md §4).
    ///
    /// <para><see cref="NotificationConfigResponse"/>'s own doc states that it has no field for the
    /// URL and deliberately no nullable one "that a future edit could start populating". This is
    /// that future edit, written down once, in the tests, purely so the absence assertions above
    /// have something they are demonstrably able to catch. Serialized with the same Web defaults
    /// the Minimal API uses, so the control body is shaped exactly like the real one.</para>
    /// </summary>
    private static string SerializeAsALeakWould(NotificationConfigResponse config) =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                config.Enabled,
                config.HasWebhookUrl,
                // The property the real record does not have, and must never grow.
                WebhookUrl = SecretWebhookUrl,
                config.ConsecutiveFailureThreshold,
                config.EnabledTriggers,
                config.LastDeliveryOutcome,
            },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    [Fact]
    public async Task The_webhook_url_never_surfaces_on_the_settings_catalog_endpoint()
    {
        // The row is colon-namespaced, so no SettingKey value can name it and
        // GET /api/admin/settings (which projects from SettingsCatalog.Entries, never from the
        // table) structurally cannot reach it. This is the property #53 established for source API
        // keys; #57 rides the same namespace to inherit it.
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var put = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            enabled = true,
            webhookUrl = SecretWebhookUrl,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // NON-VACUOUS, HALF ONE — EXISTENCE: the URL really is in the table this endpoint reads
        // from, so there is something for the projection to leak.
        await AssertWebhookUrlIsActuallyStoredAsync();

        using var settings = await SendAsync(client, HttpMethod.Get, "/api/admin/settings");
        var settingsBody = await settings.Content.ReadAsStringAsync();

        // NON-VACUOUS, HALF TWO — DETECTABILITY: existence is not enough, and this is the half the
        // original assertion was missing. AssertWebhookUrlIsActuallyStoredAsync proves the secret
        // sits in the STORE; it says nothing about whether a leak on the RESPONSE side would be
        // caught, because its control lives on the wrong side of the boundary under test. CLAUDE.md
        // §4 names exactly this shape: "asserting the fixture was created proves the secret exists.
        // It does not prove it would be detectable if it leaked."
        //
        // So build the body a LEAKY projection would have produced — the same endpoint's real
        // response with the table's own rows appended, which is precisely what projecting from the
        // Settings table instead of from SettingsCatalog.Entries would yield — and prove the two
        // searches below go red against it. Only then does their silence on the real body mean
        // anything.
        var bodyIfTheProjectionLeaked = settingsBody + await ProjectSettingsTableAsALeakWouldAsync();

        Assert.Contains(SecretWebhookUrl, bodyIfTheProjectionLeaked, StringComparison.Ordinal);
        Assert.Contains(
            NotificationRepository.WebhookUrlSettingName,
            bodyIfTheProjectionLeaked,
            StringComparison.Ordinal);

        // The real assertions, now known to be capable of failing.
        Assert.DoesNotContain(SecretWebhookUrl, settingsBody, StringComparison.Ordinal);
        Assert.DoesNotContain(NotificationRepository.WebhookUrlSettingName, settingsBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Serializes the Settings TABLE the way a leaky <c>GET /api/admin/settings</c> would — the
    /// mutation this test's control needs, produced without putting vulnerable code in the
    /// repository (CLAUDE.md §4: prove it by construction, never by mutating a file in place).
    ///
    /// <para>The real endpoint projects from <see cref="Arbitarr.Core.Settings.SettingsCatalog"/>
    /// entries, whose values are all typed (TimeSpan, bool, int, double) and therefore structurally
    /// incapable of carrying a URL. A regression that made it enumerate the table instead — the one
    /// realistic way the colon-namespaced row could ever reach the wire — would produce a body of
    /// this shape. Appending it to the real response yields a positive control that is a genuine
    /// mutation of the projection rather than a planted literal.</para>
    /// </summary>
    private async Task<string> ProjectSettingsTableAsALeakWouldAsync()
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();

        var rows = await db.Settings.AsNoTracking()
            .Select(e => new { key = e.Name, value = e.Value })
            .ToListAsync();

        return System.Text.Json.JsonSerializer.Serialize(rows);
    }

    /// <summary>
    /// The Definition-of-Done assertion: a configured webhook URL appears in NO event row and NO
    /// log row.
    ///
    /// <para>This is the leak that would matter most. <c>GET /api/activity</c> is un-gated, so a URL
    /// on an event row would be readable by any client on the network — and providers put the token
    /// in the path, so that is a credential disclosure rather than merely noisy. The test drives a
    /// real delivery attempt (the test button, against an endpoint that cannot exist) so the whole
    /// failure path — transport classification, outcome recording, the dispatcher's warning log —
    /// runs with a URL actually in hand, then sweeps every event row and the recent-log surface.</para>
    /// </summary>
    [Fact]
    public async Task A_configured_webhook_url_appears_in_no_event_row_and_no_log_row()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var put = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            enabled = true,
            webhookUrl = SecretWebhookUrl,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // NON-VACUOUS, HALF ONE: the URL really is present in the input to everything below. If
        // this ever stops holding, the absence assertions afterwards become worthless and this
        // fails first, loudly, rather than passing for the wrong reason.
        await AssertWebhookUrlIsActuallyStoredAsync();

        // Provoke a real delivery attempt so the failure path runs holding the URL. example.com's
        // /hooks path is not a webhook receiver, so this exercises classification and the outcome
        // record without depending on the outcome being any particular member.
        using var test = await SendAsync(client, HttpMethod.Post, TestRoute);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);

        var testBody = await test.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretWebhookUrl, testBody, StringComparison.Ordinal);

        // AND drive a real notifier cycle. The test button above deliberately does NOT go through
        // NotificationDispatcher (it reads the URL and posts directly), so without this the event
        // sweep below would run over rows the notification path never wrote — it would assert
        // absence from an empty set and pass no matter what the dispatcher did. Seeding enough
        // failures to cross the threshold makes the dispatcher actually deliver while holding the
        // URL, which is the code path that could put one on a row.
        using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            var events = new Arbitarr.Data.Events.EventRepository(db);

            for (var i = 0; i < 5; i++)
            {
                await events.AddAsync(
                    EventKind.SourceFailed,
                    "Source failed",
                    "timeout",
                    "placeholder-source",
                    null,
                    CancellationToken.None);
            }

            var dispatcher = new Arbitarr.Host.Notifications.NotificationDispatcher(
                events,
                scope.ServiceProvider.GetRequiredService<NotificationRepository>(),
                scope.ServiceProvider.GetRequiredService<Arbitarr.Core.Notifications.WebhookNotificationTransport>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>());

            // Non-vacuous: the cycle really did decide to notify, so the sweep below is inspecting
            // rows written while a real delivery was in flight.
            Assert.NotEmpty(await dispatcher.RunCycleAsync(CancellationToken.None));
        }

        // NON-VACUOUS, HALF TWO: no event row carries it, in the store or over the un-gated feed.
        using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            var rows = await db.Set<EventEntry>().AsNoTracking().ToListAsync();

            foreach (var row in rows)
            {
                Assert.DoesNotContain(SecretWebhookUrl, row.Summary ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretWebhookUrl, row.Reason ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretWebhookUrl, row.Detail ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(SecretWebhookUrl, row.SourceDisplayName ?? string.Empty, StringComparison.Ordinal);
            }
        }

        // Asserted UNCONDITIONALLY. This was wrapped in `if (StatusCode == OK)`, which made it the
        // one assertion here that could not fail: any non-200 skipped it silently, so the feed most
        // worth sweeping — /api/activity is deliberately UN-GATED, readable by any client on the
        // network — was checked only when it felt like answering. The endpoint is un-gated by
        // design, so 200 is the contract and a different status is itself a finding.
        using var activity = await client.GetAsync("/api/activity");
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);

        var activityBody = await activity.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretWebhookUrl, activityBody, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookTokenFragment, activityBody, StringComparison.Ordinal);

        // NON-VACUOUS, HALF THREE: no log row carries it either. Since #65 the log store is
        // PERSISTENT (a standalone arbitarr-logs.db), so a webhook URL reaching a log line is a
        // durable leak readable from the System page's Logs tab, not a transient one that scrolls
        // away — which is why the dispatcher logs the trigger and the outcome, both closed enums,
        // and never the target. Asserted over every field, not just Message: the exception text is
        // the field most likely to carry a URI.
        await FlushLogSinkAsync();

        var logStore = _factory.Services.GetRequiredService<Arbitarr.Data.Logging.LogStore>();
        var logPage = await logStore.ReadAsync(
            level: null, logger: null, page: 1, pageSize: Arbitarr.Data.Logging.LogStore.MaxPageSize);

        // Non-vacuous: the sink really is recording, so the absence assertion has something to be
        // true of rather than passing against an empty table.
        Assert.NotEmpty(logPage.Entries);

        foreach (var entry in logPage.Entries)
        {
            Assert.DoesNotContain(SecretWebhookUrl, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretWebhookUrl, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretWebhookUrl, entry.Logger, StringComparison.OrdinalIgnoreCase);

            // Also the bare token, in case a future change logs only the path or the query.
            Assert.DoesNotContain(WebhookTokenFragment, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(WebhookTokenFragment, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Waits for #65's log sink to drain. The provider batches on an interval by design (it must
    /// never write on the caller's thread), so a read taken immediately after a request can
    /// legitimately see nothing yet — which would make the assertion above vacuous.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(Arbitarr.Data.Logging.SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));

    [Fact]
    public async Task The_test_button_reports_a_distinct_outcome_without_naming_the_target()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        // With nothing configured the operator must be told so plainly rather than shown a failure.
        using var cleared = await SendAsync(client, HttpMethod.Delete, WebhookRoute);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        using var test = await SendAsync(client, HttpMethod.Post, TestRoute);
        var result = await test.Content.ReadFromJsonAsync<NotificationTestResponse>();

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal(nameof(Arbitarr.Core.Notifications.NotificationDeliveryOutcome.NotConfigured), result.Outcome);
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task An_edit_that_omits_the_url_leaves_the_configured_target_alone()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var configure = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            enabled = true,
            webhookUrl = SecretWebhookUrl,
        });
        Assert.Equal(HttpStatusCode.OK, configure.StatusCode);

        // The client never had the value, so it cannot read-and-reapply one: omitting the field
        // must not silently destroy the operator's target.
        using var edit = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            consecutiveFailureThreshold = 9,
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var config = await edit.Content.ReadFromJsonAsync<NotificationConfigResponse>();
        Assert.NotNull(config);
        Assert.True(config!.HasWebhookUrl);
        Assert.Equal(9, config.ConsecutiveFailureThreshold);
    }

    [Fact]
    public async Task Clearing_the_webhook_is_explicit_and_reported()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var configure = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            enabled = true,
            webhookUrl = SecretWebhookUrl,
        });
        Assert.Equal(HttpStatusCode.OK, configure.StatusCode);

        using var clear = await SendAsync(client, HttpMethod.Delete, WebhookRoute);
        var config = await clear.Content.ReadFromJsonAsync<NotificationConfigResponse>();

        Assert.NotNull(config);
        Assert.False(config!.HasWebhookUrl);
    }

    [Fact]
    public async Task An_out_of_range_threshold_is_rejected_with_a_reason()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            consecutiveFailureThreshold = 1,
        });

        // AC24: reject with a clear reason, never silently clamp.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("threshold", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_trigger_name_is_rejected()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, NotificationsRoute, new
        {
            enabledTriggers = new[] { "NotATrigger" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Confirms the URL is genuinely in the store, so the absence assertions that follow are
    /// testing a real presence rather than passing on an empty configuration.
    /// </summary>
    private async Task AssertWebhookUrlIsActuallyStoredAsync()
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();

        var row = await db.Settings.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Name == NotificationRepository.WebhookUrlSettingName);

        Assert.NotNull(row);
        Assert.Equal(SecretWebhookUrl, row!.Value);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    // Upsert rather than Add: the factory's SQLite database is shared across every [Fact] in this
    // IClassFixture-scoped class (Name is the SettingEntry primary key), so a second test seeding
    // the same key would collide instead of overwriting.
    private async Task SeedAdminKeyAsync()
    {
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
    }
}
