using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Media;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-u1c's Sonarr surface end to end against the real Host: read, write, clear and probe the base
/// URL and the write-only API key.
///
/// <para><b>Gating is asserted BY NAME here.</b> All four routes are concrete rather than
/// <c>{id}</c>-templated, so <see cref="AdminApiKeyRouteEnumerationTests"/>'s sweep already covers
/// them generically — but that sweep's coverage is a property of the route SHAPE, not of this
/// feature, and a later templated route on this prefix would silently drop out of it (the sweep
/// skips every <c>{</c>-containing route by design). Pinning them here means no Sonarr route is
/// gated only by assumption. The gating requests deliberately send NO body, for the same reason the
/// sweep does: a required body would be model-bound BEFORE the endpoint filter and short-circuit to
/// 400 without the gate running, letting an unauthenticated caller tell a malformed body from a
/// well-formed one and enumerate which admin routes exist. Do not "fix" the bodilessness.</para>
///
/// <para><b>THE KEY IS WRITE-ONLY, AND THE ABSENCE ASSERTIONS HERE CARRY POSITIVE CONTROLS.</b>
/// <c>Assert.DoesNotContain(secret, body)</c> passes just as happily when the secret was never in
/// play — an empty set contains nothing (CLAUDE.md §4). Every such assertion below is therefore
/// preceded by a demonstration that a planted secret WOULD be found by the same search: the tests
/// search the response for a control string that IS present before asserting the key is not. Merely
/// asserting the fixture was created (a 200, a non-null value) proves the secret EXISTS; it does not
/// prove it would be DETECTABLE if it leaked, and only the second makes the assertion bite.</para>
///
/// <para>All addresses are RFC 5737 documentation forms and all credential-shaped strings are
/// <c>placeholder-*</c>: no real address or secret enters committed content.</para>
/// </summary>
public sealed class AdminArrEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string SonarrRoute = AdminArrEndpoints.SonarrRoute;
    private const string SonarrTestRoute = AdminArrEndpoints.SonarrTestRoute;

    /// <summary>
    /// Distinctive enough that a substring search over a whole response body cannot match it by
    /// accident, which is what makes the "it is not in here" assertions meaningful.
    /// </summary>
    private const string SonarrKey = "placeholder-sonarr-key-4f2b9c1e";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminArrEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", SonarrRoute)]
    [InlineData("PUT", SonarrRoute)]
    [InlineData("DELETE", SonarrRoute)]
    [InlineData("POST", SonarrTestRoute)]
    public async Task Every_sonarr_route_rejects_a_request_without_the_admin_key(string method, string path)
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        // No body, deliberately — see the type doc. Do not "fix" this by adding one.
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
            $"Expected {method} {path} to be admin-gated, but it returned {(int)response.StatusCode}.");
    }

    /// <summary>
    /// The gate is by path prefix, never by verb: the read is admin configuration, not a dashboard
    /// fact, so GET is gated exactly like the writes.
    /// </summary>
    [Fact]
    public async Task The_gate_is_by_path_prefix_rather_than_by_verb_so_the_read_is_gated_too()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var unkeyed = await client.GetAsync(SonarrRoute);
        Assert.True(unkeyed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable);

        using var keyed = await SendAsync(client, HttpMethod.Get, SonarrRoute);
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
    }

    /// <summary>
    /// A missing body must be a 400 from inside the handler, not a 400 from model binding ahead of
    /// the gate. With the key presented both look the same to this test — which is the point: the
    /// gating theory above proves the unkeyed case never reaches binding at all.
    /// </summary>
    [Fact]
    public async Task A_put_with_no_body_is_rejected_by_the_handler()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, SonarrRoute);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_base_url_and_key_are_persisted_and_the_url_is_readable()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var write = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.60:8989", apiKey = SonarrKey });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        using var read = await SendAsync(client, HttpMethod.Get, SonarrRoute);
        var config = await read.Content.ReadFromJsonAsync<ArrConfigResponse>();

        Assert.NotNull(config);
        // The URL is deliberately readable: it is not a credential, and ValidateBaseUrl rejecting
        // userinfo is what keeps that true. The key is reported as a bool and never as a value.
        Assert.Equal("http://192.0.2.60:8989", config!.BaseUrl);
        Assert.True(config.HasApiKey);
    }

    /// <summary>
    /// THE WRITE-ONLY ASSERTION, WITH ITS POSITIVE CONTROL. The control is the BASE URL: it is
    /// written in the same request as the key, is deliberately readable, and is a distinctive string
    /// — so finding it in the response body proves this search over this body actually finds things
    /// that are there. Only then does the key's absence mean the key is not there, rather than
    /// meaning the body was empty or the search was looking at the wrong thing.
    /// </summary>
    [Fact]
    public async Task The_stored_key_never_comes_back_on_the_read()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        const string ControlUrl = "http://192.0.2.61:8989";

        using var write = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = ControlUrl, apiKey = SonarrKey });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        using var read = await SendAsync(client, HttpMethod.Get, SonarrRoute);
        var body = await read.Content.ReadAsStringAsync();

        // POSITIVE CONTROL: a value written by the same request IS found by this search.
        Assert.Contains(ControlUrl, body, StringComparison.Ordinal);
        // ...therefore this absence is a real absence.
        Assert.DoesNotContain(SonarrKey, body, StringComparison.OrdinalIgnoreCase);

        // And the same for the write's own echo, which is a second place the value could surface.
        var writeBody = await write.Content.ReadAsStringAsync();
        Assert.Contains(ControlUrl, writeBody, StringComparison.Ordinal);
        Assert.DoesNotContain(SonarrKey, writeBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE MECHANISM FROM CLAUDE.md §1, ASSERTED. The key's row name is colon-namespaced, which no
    /// <see cref="SettingKey"/> value can produce, and that is WHY it cannot surface on the settings
    /// catalog projection. This asserts the consequence rather than the mechanism, so a future
    /// change that adds a <c>SettingKey</c> able to name the row fails here.
    ///
    /// <para>POSITIVE CONTROL: the settings response is first shown to contain a value that IS in
    /// the catalog, proving the search finds what is present in this particular body.</para>
    /// </summary>
    [Fact]
    public async Task The_stored_key_never_appears_on_the_settings_catalog_projection()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var write = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.62:8989", apiKey = SonarrKey });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        using var settings = await SendAsync(client, HttpMethod.Get, "/api/admin/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var body = await settings.Content.ReadAsStringAsync();

        // POSITIVE CONTROL: a key that IS in the catalog appears in this projection, so a search
        // over this body demonstrably finds setting names that are present.
        Assert.Contains(SettingKey.FreshUntil.ToString(), body, StringComparison.Ordinal);

        // ...therefore both of these absences are real. The row NAME as well as the value: a
        // projection that leaked the name would tell a caller the row exists to be attacked.
        Assert.DoesNotContain(SonarrKey, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ArrInstanceRepository.SonarrApiKeySettingName, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A null key means "leave the stored value alone" — the source-API-key contract exactly. The
    /// client never had the value, so it cannot read-and-reapply it, and an edit that changes only
    /// the address must not clear the key as a side effect.
    /// </summary>
    [Fact]
    public async Task An_edit_that_omits_the_key_leaves_the_stored_key_in_place()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var initial = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.63:8989", apiKey = SonarrKey });
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        // Address only — no apiKey property at all.
        using var edit = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.64:8989" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var config = await edit.Content.ReadFromJsonAsync<ArrConfigResponse>();
        Assert.NotNull(config);
        Assert.Equal("http://192.0.2.64:8989", config!.BaseUrl);
        Assert.True(config.HasApiKey);

        // Asserted at the row level too, not only through the projection: the projection could
        // report true from a stale read while the row was gone.
        Assert.Equal(
            SonarrKey,
            await ReadStoredValueAsync(ArrInstanceRepository.SonarrApiKeySettingName));
    }

    /// <summary>
    /// THE DELETE UNCONFIGURES THE WHOLE INSTANCE, NOT THE KEY ALONE (ADR 0010; bead arb-c26).
    /// A secret is cleared only by deleting the thing that owns it, so both rows go together.
    ///
    /// <para>Asserted PER ROW rather than through the projection alone: the projection could report
    /// the right shape from one row having gone while the other survived, and "the configuration is
    /// cleared" must mean both. Each per-row assertion carries a POSITIVE CONTROL — the row is shown
    /// to be PRESENT first — so an absence afterwards is a real deletion rather than a row that was
    /// never written.</para>
    /// </summary>
    [Fact]
    public async Task The_delete_unconfigures_the_instance_removing_both_rows()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var write = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.65:8989", apiKey = SonarrKey });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        // POSITIVE CONTROLS, per row: both rows really are there before the delete, so the
        // assertions after it are about a deletion and not about a write that never happened.
        Assert.Equal(SonarrKey, await ReadStoredValueAsync(ArrInstanceRepository.SonarrApiKeySettingName));
        Assert.Equal(
            "http://192.0.2.65:8989",
            await ReadStoredValueAsync(ArrInstanceRepository.SonarrBaseUrlSettingName));

        using var clear = await SendAsync(client, HttpMethod.Delete, SonarrRoute);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        // ...therefore both of these absences are real, and each is asserted on its own row.
        Assert.Null(await ReadStoredValueAsync(ArrInstanceRepository.SonarrApiKeySettingName));
        Assert.Null(await ReadStoredValueAsync(ArrInstanceRepository.SonarrBaseUrlSettingName));

        // And the response states the unconfigured shape rather than leaving the caller to infer it.
        var config = await clear.Content.ReadFromJsonAsync<ArrConfigResponse>();
        Assert.NotNull(config);
        Assert.False(config!.HasApiKey);
        Assert.True(string.IsNullOrEmpty(config.BaseUrl));

        // A subsequent GET agrees, so the delete is durable rather than only reflected in the
        // response the delete itself composed.
        using var read = await SendAsync(client, HttpMethod.Get, SonarrRoute);
        var afterwards = await read.Content.ReadFromJsonAsync<ArrConfigResponse>();
        Assert.NotNull(afterwards);
        Assert.False(afterwards!.HasApiKey);
        Assert.True(string.IsNullOrEmpty(afterwards.BaseUrl));
    }

    /// <summary>
    /// There is deliberately NO key-only clear route (ADR 0010): it would leave an address with no
    /// credential, the half-configured state the Sources surface never offers. Asserted rather than
    /// merely omitted, so re-adding one is a test failure and a deliberate decision rather than an
    /// unnoticed convenience.
    ///
    /// <para>404 or 405 — the route not existing, or existing without that verb — both mean the
    /// affordance is absent, which is what this pins. Not gated behind the admin key on purpose:
    /// this is about the route's existence, and it must be absent for an authenticated caller too.</para>
    /// </summary>
    [Fact]
    public async Task There_is_no_key_only_clear_route()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Delete, $"{SonarrRoute}/key");

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"Expected no key-only clear route, but DELETE {SonarrRoute}/key returned {(int)response.StatusCode}.");
    }

    /// <summary>
    /// THE VALIDATION THAT KEEPS "THE BASE URL IS NOT A SECRET" TRUE. A URL carrying userinfo would
    /// smuggle a credential into a value this feature serves back on the GET and lets
    /// <c>IHttpClientFactory</c> log in full. Rejecting it is what earns the readable URL.
    ///
    /// <para>The rejection must not echo the credential it rejected — asserted with a positive
    /// control, since a response body that failed to arrive would satisfy the absence trivially.</para>
    /// </summary>
    [Fact]
    public async Task A_base_url_carrying_credentials_is_rejected_without_echoing_them()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = $"http://operator:{SonarrKey}@192.0.2.66:8989" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        // POSITIVE CONTROL: the body is a real error payload, so a search over it finds text that
        // is present. Without this, the absence below would pass on an empty body.
        Assert.Contains("error", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SonarrKey, body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://192.0.2.67:8989")]
    [InlineData("")]
    public async Task A_malformed_base_url_is_rejected_rather_than_coerced(string baseUrl)
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, SonarrRoute, new { baseUrl });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A submitted BLANK key is a different thing from an omitted one and is rejected, so the two
    /// stay distinguishable — an operator cannot accidentally store an empty credential and then
    /// see "set" beside it.
    /// </summary>
    [Fact]
    public async Task A_blank_key_is_rejected_rather_than_stored()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.68:8989", apiKey = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Reject-never-clamp is also reject-never-PARTIALLY-apply: a rejected key must not leave a
    /// saved address behind it, or one PUT half-succeeds and the operator cannot tell which half
    /// from a single error message. This is the ruling <c>AdminAiEndpoints</c> states for its own
    /// pair, asserted here for this one.
    /// </summary>
    [Fact]
    public async Task A_rejected_key_does_not_leave_the_address_written()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var seed = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.69:8989", apiKey = SonarrKey });
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using var rejected = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.70:8989", apiKey = "" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using var read = await SendAsync(client, HttpMethod.Get, SonarrRoute);
        var config = await read.Content.ReadFromJsonAsync<ArrConfigResponse>();

        Assert.NotNull(config);
        // The address the rejected request carried was NOT applied.
        Assert.Equal("http://192.0.2.69:8989", config!.BaseUrl);
    }

    /// <summary>
    /// The probe answers on the STORED values, so with nothing configured it must say so rather than
    /// probe an empty address.
    /// </summary>
    [Fact]
    public async Task The_probe_reports_a_missing_configuration_rather_than_probing_nothing()
    {
        await SeedAdminKeyAsync();
        await ClearConfigurationAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, SonarrTestRoute);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The probe's answer is a closed enum plus wording derived from that enum alone, so no probe
    /// failure path can carry the key. The address here is an RFC 5737 documentation form that
    /// nothing answers on, so the outcome is a reachability failure — which is exactly the path most
    /// likely to interpolate an exception message carrying the request URI, and therefore the key.
    /// </summary>
    [Fact]
    public async Task A_failing_probe_reports_a_closed_outcome_and_never_the_key()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var write = await SendAsync(
            client,
            HttpMethod.Put,
            SonarrRoute,
            new { baseUrl = "http://192.0.2.71:8989", apiKey = SonarrKey });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);

        using var response = await SendAsync(client, HttpMethod.Post, SonarrTestRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ArrTestResponse>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.False(result!.Success);
        // The outcome is one of the closed enum's names, never free text.
        Assert.Contains(result.Outcome, Enum.GetNames<Arbitarr.Core.Sources.SourceProbeOutcome>());

        // POSITIVE CONTROL: the body is a real probe payload carrying the outcome name, so a search
        // over it finds what is present. Only then is the key's absence meaningful.
        Assert.Contains(result.Outcome, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SonarrKey, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads one Settings row by name, so an assertion can be made PER ROW rather than through a
    /// projection that could hide one row's survival behind another's absence.
    /// </summary>
    private async Task<string?> ReadStoredValueAsync(string name)
    {
        string? stored = null;
        await _factory.SeedAsync(async db =>
        {
            stored = await db.Settings
                .AsNoTracking()
                .Where(e => e.Name == name)
                .Select(e => e.Value)
                .FirstOrDefaultAsync();
        });
        return stored;
    }

    private async Task ClearConfigurationAsync() =>
        await _factory.SeedAsync(async db =>
        {
            var rows = await db.Settings
                .Where(e => e.Name == ArrInstanceRepository.SonarrApiKeySettingName
                    || e.Name == ArrInstanceRepository.SonarrBaseUrlSettingName)
                .ToListAsync();
            db.Settings.RemoveRange(rows);
        });

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
