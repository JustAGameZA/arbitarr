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
/// <para>Every leak assertion is <b>non-vacuous</b>: it establishes that the URL really is stored
/// (via the store, and via the surface's own <c>hasWebhookUrl</c> indicator) BEFORE asserting it is
/// absent from responses, event rows and log rows. Without that first half a passing assertion
/// would prove only that nothing was ever configured.</para>
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

        // NON-VACUOUS: the URL really was accepted and stored — the surface says so itself...
        Assert.NotNull(config);
        Assert.True(config!.HasWebhookUrl);

        // ...and yet the response that reports its presence does not contain the value.
        Assert.DoesNotContain(SecretWebhookUrl, body, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder-super-secret-webhook-token", body, StringComparison.Ordinal);

        // The same on the read path, which is the projection an operator's browser actually loads.
        using var get = await SendAsync(client, HttpMethod.Get, NotificationsRoute);
        var readBody = await get.Content.ReadAsStringAsync();

        Assert.Contains("\"hasWebhookUrl\":true", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretWebhookUrl, readBody, StringComparison.Ordinal);
    }

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

        // NON-VACUOUS: prove it is stored before proving the catalog does not show it.
        await AssertWebhookUrlIsActuallyStoredAsync();

        using var settings = await SendAsync(client, HttpMethod.Get, "/api/admin/settings");
        var settingsBody = await settings.Content.ReadAsStringAsync();

        Assert.DoesNotContain(SecretWebhookUrl, settingsBody, StringComparison.Ordinal);
        Assert.DoesNotContain(NotificationRepository.WebhookUrlSettingName, settingsBody, StringComparison.Ordinal);
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

        using var activity = await client.GetAsync("/api/activity");
        if (activity.StatusCode == HttpStatusCode.OK)
        {
            var activityBody = await activity.Content.ReadAsStringAsync();
            Assert.DoesNotContain(SecretWebhookUrl, activityBody, StringComparison.Ordinal);
        }

        // NON-VACUOUS, HALF THREE: no log row carries it either. Logs are surfaced in the UI, so a
        // URL in a warning line is the same disclosure by another route — which is why the
        // dispatcher logs the trigger and the outcome (both closed enums) and never the target.
        using var log = await client.GetAsync("/api/searches/recent");
        if (log.StatusCode == HttpStatusCode.OK)
        {
            var logBody = await log.Content.ReadAsStringAsync();
            Assert.DoesNotContain(SecretWebhookUrl, logBody, StringComparison.Ordinal);
        }
    }

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
