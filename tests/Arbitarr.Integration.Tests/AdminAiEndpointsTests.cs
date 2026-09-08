using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #89's AI backend surface end to end against the real Host: read, write and probe the Ollama base
/// URL.
///
/// <para><b>Gating is asserted BY NAME here.</b> All three routes are concrete rather than
/// <c>{id}</c>-templated, so <see cref="AdminApiKeyRouteEnumerationTests"/>'s sweep already covers
/// them generically — but that sweep's coverage is a property of the route shape, not of this
/// feature, and a later templated route on this prefix would silently drop out of it. Pinning them
/// here means no AI route is gated only by assumption. The gating requests deliberately send NO
/// body, for the same reason the sweep does: a required body would be model-bound BEFORE the
/// endpoint filter and short-circuit to 400 without the gate running, letting an unauthenticated
/// caller tell a malformed body from a well-formed one and enumerate which admin routes exist.</para>
///
/// <para><b>Unlike the source and notification surfaces, this one has no secret to keep.</b> Ollama
/// has no authentication and the base URL carries no token, so the value is deliberately served
/// back on the GET — there is no write-only idiom to assert here, and adding absence assertions for
/// a value that is meant to be readable would be theatre. What IS asserted instead is the
/// validation that keeps "not a secret" TRUE: a URL carrying credentials is rejected, and the
/// rejection does not echo the credential it rejected.</para>
///
/// <para>All addresses are RFC 5737 documentation forms and all credential-shaped strings are
/// <c>placeholder-*</c>: no real address or secret enters committed content.</para>
/// </summary>
public sealed class AdminAiEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string OllamaRoute = AdminAiEndpoints.OllamaRoute;
    private const string OllamaTestRoute = AdminAiEndpoints.OllamaTestRoute;

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminAiEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", OllamaRoute)]
    [InlineData("PUT", OllamaRoute)]
    [InlineData("POST", OllamaTestRoute)]
    public async Task Every_ai_route_rejects_a_request_without_the_admin_key(string method, string path)
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

        using var unkeyed = await client.GetAsync(OllamaRoute);
        Assert.True(unkeyed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable);

        using var keyed = await SendAsync(client, HttpMethod.Get, OllamaRoute);
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
    }

    /// <summary>
    /// A missing body must be a 400 from inside the handler, not a 400 from model binding ahead of
    /// the gate. With the key presented, both look the same to this test — which is the point: the
    /// gating theory above proves the unkeyed case never reaches binding at all.
    /// </summary>
    [Fact]
    public async Task A_put_with_no_body_is_rejected_by_the_handler()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, OllamaRoute);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_configured_base_url_is_readable()
    {
        await SeedAdminKeyAsync();
        await SetBaseUrlAsync("http://192.0.2.50:11434");
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, OllamaRoute);
        var config = await response.Content.ReadFromJsonAsync<OllamaConfigResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(config);
        // Deliberately readable: it is not a secret. See the type doc.
        Assert.Equal("http://192.0.2.50:11434", config!.BaseUrl);
    }

    [Fact]
    public async Task A_valid_base_url_is_persisted_and_returned()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(
            client, HttpMethod.Put, OllamaRoute, new { baseUrl = "http://192.0.2.60:11434" });
        var config = await response.Content.ReadFromJsonAsync<OllamaConfigResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://192.0.2.60:11434", config!.BaseUrl);
        Assert.Equal("http://192.0.2.60:11434", await ReadStoredBaseUrlAsync());
    }

    /// <summary>
    /// <b>The "no restart" assertion at the HTTP boundary.</b> A write followed by a read must
    /// report the NEW value. That only holds because the write invalidates the base-URL cache; a
    /// regression that dropped the invalidation would serve the previous address here while the row
    /// itself was correct — which is precisely the silent failure mode this pins.
    /// </summary>
    [Fact]
    public async Task A_write_is_visible_to_the_very_next_read_without_a_restart()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        await SendAsync(client, HttpMethod.Put, OllamaRoute, new { baseUrl = "http://192.0.2.70:11434" });
        using var first = await SendAsync(client, HttpMethod.Get, OllamaRoute);
        var before = await first.Content.ReadFromJsonAsync<OllamaConfigResponse>();

        await SendAsync(client, HttpMethod.Put, OllamaRoute, new { baseUrl = "http://192.0.2.71:11434" });
        using var second = await SendAsync(client, HttpMethod.Get, OllamaRoute);
        var after = await second.Content.ReadFromJsonAsync<OllamaConfigResponse>();

        Assert.Equal("http://192.0.2.70:11434", before!.BaseUrl);
        Assert.Equal("http://192.0.2.71:11434", after!.BaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://ollama.example.com")]
    [InlineData("http://ollama.example.invalid")]
    public async Task An_invalid_base_url_is_rejected_and_nothing_is_written(string proposed)
    {
        await SeedAdminKeyAsync();
        await SetBaseUrlAsync("http://192.0.2.80:11434");
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Put, OllamaRoute, new { baseUrl = proposed });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Reject, never clamp, and never a partial write.
        Assert.Equal("http://192.0.2.80:11434", await ReadStoredBaseUrlAsync());
    }

    /// <summary>
    /// A URL carrying credentials is rejected, and the rejection does not echo them back.
    ///
    /// <para>POSITIVE CONTROL, both halves. <b>Existence</b>: the credential really is in the
    /// request, proven by the request body itself carrying it. <b>Detectability</b>: the same
    /// substring search is first shown to go RED against a body built the way a leaking
    /// implementation would build it, so a pass below means the value is genuinely absent rather
    /// than that the search could never have found it. CLAUDE.md §4 is explicit that the first does
    /// not imply the second.</para>
    /// </summary>
    [Fact]
    public async Task A_url_with_credentials_is_rejected_without_echoing_them()
    {
        const string password = "placeholder-super-secret-password";
        var proposed = $"http://user:{password}@192.0.2.90:11434";

        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        // Detectability control: a response built as a leaking implementation would build it IS
        // found by this search, so the assertion below can actually fail.
        var wouldLeak = $"{{\"error\":\"'{proposed}' is not a valid Ollama base URL.\"}}";
        Assert.Contains(password, wouldLeak, StringComparison.Ordinal);

        using var response = await SendAsync(client, HttpMethod.Put, OllamaRoute, new { baseUrl = proposed });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        Assert.NotEqual(proposed, await ReadStoredBaseUrlAsync());
    }

    /// <summary>
    /// The probe reports a closed outcome and fixed wording. Against an address nothing is serving,
    /// the honest answer is Unreachable — and critically the response carries no free-text field
    /// that could relay an exception message or the address itself.
    /// </summary>
    [Fact]
    public async Task The_probe_reports_a_closed_outcome_and_never_the_configured_address()
    {
        // TEST-NET-1 with a port nothing listens on: unreachable by construction, no real host.
        const string unreachable = "http://192.0.2.99:11434";
        await SeedAdminKeyAsync();
        await SetBaseUrlAsync(unreachable);
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, OllamaTestRoute);
        var body = await response.Content.ReadAsStringAsync();
        var result = System.Text.Json.JsonSerializer.Deserialize<OllamaTestResponse>(
            body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(result);
        Assert.False(result!.Success);
        // A closed enum name, not free text.
        Assert.Contains(result.Outcome, Enum.GetNames<Arbitarr.Core.Ai.OllamaProbeOutcome>());
        Assert.NotEqual(nameof(Arbitarr.Core.Ai.OllamaProbeOutcome.Ok), result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));

        // Detectability control: a response that DID carry the address would be found by this
        // search, so its absence below is a real property rather than a search that cannot see.
        var wouldLeak = $"{{\"outcome\":\"Unreachable\",\"message\":\"could not reach {unreachable}\"}}";
        Assert.Contains("192.0.2.99", wouldLeak, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.99", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The key stays OFF the settings catalog, which is what keeps the generic settings surface from
    /// rendering it a second time as an unexplained text field — and, because the catalog feeds both
    /// the PUT allow-list and the GET projection, keeps that route a 404.
    /// </summary>
    [Fact]
    public async Task The_base_url_is_not_writable_through_the_generic_settings_route()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await SendAsync(
            client,
            HttpMethod.Put,
            $"/api/admin/settings/{SettingKey.OllamaBaseUrl}",
            new { value = "http://192.0.2.100:11434" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_base_url_is_not_listed_on_the_generic_settings_surface()
    {
        await SeedAdminKeyAsync();
        await SetBaseUrlAsync("http://192.0.2.110:11434");
        using var client = _factory.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, "/api/admin/settings");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Detectability control: the catalog body genuinely is searchable — it carries the other
        // AI-group keys — so a miss below means this key is absent, not that the search is blind.
        Assert.Contains(nameof(SettingKey.AiKillSwitch), body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(SettingKey.OllamaBaseUrl), body, StringComparison.Ordinal);
    }

    private async Task<string?> ReadStoredBaseUrlAsync()
    {
        string? stored = null;
        await _factory.SeedAsync(async db =>
        {
            stored = await db.Settings
                .AsNoTracking()
                .Where(e => e.Name == SettingKey.OllamaBaseUrl.ToString())
                .Select(e => e.Value)
                .FirstOrDefaultAsync();
        });
        return stored;
    }

    private async Task SetBaseUrlAsync(string baseUrl)
    {
        await _factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.OllamaBaseUrl.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.OllamaBaseUrl.ToString(),
                    Value = baseUrl,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = baseUrl;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });

        // The cache is a singleton that outlives a request by design, so a test writing the row
        // directly must invalidate it exactly as the endpoint's write path does — otherwise the
        // read under test would legitimately serve the previously-cached address.
        _factory.Services.GetRequiredService<Arbitarr.Core.Ai.OllamaBaseUrlCache>().Invalidate();
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
