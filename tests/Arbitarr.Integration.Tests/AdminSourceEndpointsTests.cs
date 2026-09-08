using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #53 stage 53c: the admin sources CRUD surface end to end against the real Host.
///
/// <para>The plan's §5 names three things 53c's tests must establish. Two of them live here — that
/// every route is admin-gated, and that <b>a stored secret never appears in any response body</b> —
/// while the four-failure-mode requirement is pinned in
/// <c>Arbitarr.Core.Tests.SourceConnectivityProberTests</c> against the prober itself, where each
/// mode can actually be provoked. The generic "every classified route is gated" sweep lives in
/// <see cref="AdminApiKeyRouteEnumerationTests"/>; the per-route assertions here additionally cover
/// the templated routes (<c>{id}</c>) that the sweep deliberately skips, so no route in this file
/// is gated only by assumption.</para>
///
/// <para>All addresses are 192.0.2.x (RFC 5737 documentation range) and all key material is
/// <c>placeholder-*</c>: no real host or secret ever enters committed content.</para>
/// </summary>
public sealed class AdminSourceEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string SourcesRoute = "/api/admin/sources";

    /// <summary>
    /// The value the leak assertions hunt for. Distinctive on purpose: a substring search for it
    /// across a whole response body cannot collide with anything else the payload legitimately
    /// contains, so a hit is unambiguously the secret escaping.
    /// </summary>
    private const string SecretApiKey = "placeholder-super-secret-source-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminSourceEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", SourcesRoute)]
    [InlineData("POST", SourcesRoute)]
    [InlineData("PUT", SourcesRoute + "/1")]
    [InlineData("DELETE", SourcesRoute + "/1")]
    [InlineData("POST", SourcesRoute + "/1/test")]
    public async Task Every_source_route_requires_the_admin_key(string method, string path)
    {
        // AC6/D2. This covers the {id}-templated routes by name, which
        // AdminApiKeyRouteEnumerationTests skips because they cannot be resolved generically.
        // The key is seeded first so this deterministically exercises the keyed gate (401) rather
        // than the unconfigured fail-closed path (503), whose reachability depends on which other
        // [Fact] in this IClassFixture-scoped class ran first.
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_stored_api_key_never_appears_in_any_response_body()
    {
        // §5 / AC2, the single most important assertion in this file. It deliberately sweeps EVERY
        // response body the surface can produce for a source that has a key stored — create, list,
        // update, and test — and searches the raw text rather than a deserialized field, so a leak
        // through an unexpected property name, a serialized exception, or an error message is
        // caught just as well as a leak through a field someone added to SourceResponse.
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var bodies = new List<(string Label, string Body)>();

        using var createResponse = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = "NzbHydra",
            displayName = "Leak probe " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.30:5076",
            apiKey = SecretApiKey,
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        bodies.Add(("POST (create)", await createResponse.Content.ReadAsStringAsync()));

        var created = await createResponse.Content.ReadFromJsonAsync<SourceResponse>();
        Assert.NotNull(created);

        using var listResponse = await client.GetAsync(SourcesRoute);
        bodies.Add(("GET (list)", await listResponse.Content.ReadAsStringAsync()));

        using var updateResponse = await client.PutAsJsonAsync($"{SourcesRoute}/{created!.Id}", new
        {
            kind = created.Kind,
            displayName = created.DisplayName,
            baseUrl = created.BaseUrl,
            enabled = false,
        });
        bodies.Add(("PUT (update)", await updateResponse.Content.ReadAsStringAsync()));

        // The test endpoint is the most dangerous path for a leak: it is the one place that reads
        // the key back out of storage in order to send it upstream. 192.0.2.30 is unroutable, so
        // this exercises the failure path — precisely where a careless implementation would echo
        // the key into an error message.
        using var testResponse = await client.PostAsync($"{SourcesRoute}/{created.Id}/test", content: null);
        bodies.Add(("POST (test)", await testResponse.Content.ReadAsStringAsync()));

        // And the settings surface, which must never carry the source:{id}:api_key row: that name
        // cannot be produced by any SettingKey enum value and GET /api/admin/settings projects from
        // SettingsCatalog.Entries rather than from the table. This is the #43 trap, asserted.
        using var settingsResponse = await client.GetAsync("/api/admin/settings");
        bodies.Add(("GET (admin settings)", await settingsResponse.Content.ReadAsStringAsync()));

        foreach (var (label, body) in bodies)
        {
            Assert.DoesNotContain(SecretApiKey, body, StringComparison.Ordinal);
        }

        // Prove the sweep is not vacuous: the key really was stored, so the assertions above ran
        // against a source that HAD a secret rather than one that never had one.
        Assert.True(created.HasApiKey);
    }

    [Fact]
    public async Task The_list_reports_key_presence_as_a_boolean_indicator()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var withKey = await CreateSourceAsync(client, "With key " + Guid.NewGuid().ToString("N"), SecretApiKey);
        var withoutKey = await CreateSourceAsync(client, "Without key " + Guid.NewGuid().ToString("N"), apiKey: null);

        var sources = await client.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);
        Assert.NotNull(sources);

        Assert.True(sources!.Single(s => s.Id == withKey.Id).HasApiKey);
        Assert.False(sources.Single(s => s.Id == withoutKey.Id).HasApiKey);
    }

    [Fact]
    public async Task Creating_a_source_persists_it_and_returns_201()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var name = "Created " + Guid.NewGuid().ToString("N");
        var created = await CreateSourceAsync(client, name, SecretApiKey);

        Assert.Equal("NzbHydra", created.Kind);
        Assert.Equal(name, created.DisplayName);
        Assert.True(created.Enabled);

        var sources = await client.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);
        Assert.Contains(sources!, s => s.Id == created.Id);
    }

    [Fact]
    public async Task Updating_a_source_changes_it_and_can_disable_it_without_deleting_it()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var created = await CreateSourceAsync(client, "Updatable " + Guid.NewGuid().ToString("N"), SecretApiKey);
        var renamed = "Renamed " + Guid.NewGuid().ToString("N");

        using var response = await client.PutAsJsonAsync($"{SourcesRoute}/{created.Id}", new
        {
            kind = created.Kind,
            displayName = renamed,
            baseUrl = "http://192.0.2.31:5076",
            enabled = false,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<SourceResponse>();

        Assert.Equal(renamed, updated!.DisplayName);
        Assert.Equal("http://192.0.2.31:5076", updated.BaseUrl);
        Assert.False(updated.Enabled);

        // Omitting apiKey leaves the stored one in force — the write-only contract means the client
        // never had the value to send back, so "unchanged" must be expressible by omission.
        Assert.True(updated.HasApiKey);
    }

    [Fact]
    public async Task Updating_a_source_with_apiKey_omitted_preserves_the_stored_key_value()
    {
        // Updating_a_source_changes_it_and_can_disable_it_without_deleting_it only asserts the
        // HasApiKey boolean, which a write path that overwrote the row with a different (but still
        // non-null) value would satisfy just as happily. This test is the positive control: it
        // reads the actual stored row before and after the omitted-apiKey update and asserts the
        // value itself, not just its presence, survived unchanged (CLAUDE.md §4).
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var created = await CreateSourceAsync(client, "Key-preserving " + Guid.NewGuid().ToString("N"), SecretApiKey);

        using var response = await client.PutAsJsonAsync($"{SourcesRoute}/{created.Id}", new
        {
            kind = created.Kind,
            displayName = created.DisplayName,
            baseUrl = "http://192.0.2.32:5076",
            // apiKey omitted on purpose — this is the "leave alone" contract under test.
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await _factory.SeedAsync(db =>
        {
            var row = db.Settings.Find(SourceRepository.ApiKeySettingName(created.Id));
            Assert.NotNull(row);
            Assert.Equal(SecretApiKey, row!.Value);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Deleting_a_source_also_removes_its_stored_key()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var created = await CreateSourceAsync(client, "Deletable " + Guid.NewGuid().ToString("N"), SecretApiKey);

        using var response = await client.DeleteAsync($"{SourcesRoute}/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var sources = await client.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);
        Assert.DoesNotContain(sources!, s => s.Id == created.Id);

        // The secret must not outlive the source it belonged to: an orphaned row would be dead
        // weight in a backup (§3.1's consequence flagged forward to #56) and could be silently
        // re-adopted if the id were ever reissued.
        await _factory.SeedAsync(db =>
        {
            var orphan = db.Settings.Find(SourceRepository.ApiKeySettingName(created.Id));
            Assert.Null(orphan);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_unknown_id_is_404_on_update_delete_and_test()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        const long MissingId = 987654321;

        using var update = await client.PutAsJsonAsync($"{SourcesRoute}/{MissingId}", new
        {
            kind = "NzbHydra",
            displayName = "Nope",
            baseUrl = "http://192.0.2.40:5076",
        });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        using var delete = await client.DeleteAsync($"{SourcesRoute}/{MissingId}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        using var test = await client.PostAsync($"{SourcesRoute}/{MissingId}/test", content: null);
        Assert.Equal(HttpStatusCode.NotFound, test.StatusCode);
    }

    [Fact]
    public async Task Malformed_input_is_rejected_with_400_rather_than_coerced()
    {
        // AC24 posture, enforced by SourceRepository and merely translated here: a bad URL is
        // rejected, never clamped into something valid.
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = "NzbHydra",
            displayName = "Bad URL " + Guid.NewGuid().ToString("N"),
            baseUrl = "not-a-url",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_body_is_rejected_by_the_handler_rather_than_by_model_binding()
    {
        // The required-body trap, asserted from the inside. Both body-taking routes must bind their
        // body OPTIONALLY and null-check it in the handler: a REQUIRED body is model-bound BEFORE
        // endpoint filters run, so a bodiless request would short-circuit to 400 without
        // AdminApiKeyFilter ever executing, letting an unauthenticated remote caller tell a
        // malformed body (400) from a well-formed one (503) and enumerate the admin surface from
        // outside the gate.
        //
        // Reaching 400 *while authenticated* is the positive half of that property: the request got
        // past the gate and into the handler. The negative half — that an UNAUTHENTICATED bodiless
        // request is still refused by the filter — is what AdminApiKeyRouteEnumerationTests sweeps,
        // which is exactly why it sends no body.
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var create = await client.PostAsync(SourcesRoute, content: null);
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        using var update = await client.PutAsync($"{SourcesRoute}/1", content: null);
        Assert.Contains(update.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
    }

    [Fact]
    public async Task The_test_endpoint_reports_a_distinct_outcome_rather_than_a_bare_failure()
    {
        // AC4 at the API boundary: the endpoint must surface WHICH failure happened. 192.0.2.x is
        // unroutable by definition (RFC 5737), so this is the unreachable case, and it must come
        // back named — not as a generic "failed".
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var created = await CreateSourceAsync(client, "Unreachable " + Guid.NewGuid().ToString("N"), SecretApiKey);

        using var response = await client.PostAsync($"{SourcesRoute}/{created.Id}/test", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SourceTestResponse>();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("Unreachable", result.Outcome);
        Assert.NotEmpty(result.Message);
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
    }

    private static async Task<SourceResponse> CreateSourceAsync(HttpClient client, string displayName, string? apiKey)
    {
        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = "NzbHydra",
            displayName,
            baseUrl = "http://192.0.2.30:5076",
            apiKey,
            enabled = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SourceResponse>())!;
    }

    // Upsert rather than Add: the factory's SQLite database is shared across every [Fact] in this
    // IClassFixture-scoped class (Name is the SettingEntry primary key), so a second test seeding
    // the same key would otherwise hit a unique-constraint violation instead of overwriting.
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
