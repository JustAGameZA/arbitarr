using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Security;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Security;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #58: the <c>/api/admin/keys</c> CRUD surface end to end against the real Host — create, list,
/// revoke, the one-time plaintext reveal, and the AC5 lockout refusal.
///
/// <para>Each [Fact] gets its OWN factory rather than sharing an <c>IClassFixture</c>, because these
/// tests mint and revoke keys and the gate reads live state: a test that revoked the last admin key
/// would change what the next one sees. Every other admin endpoint test in this project can share a
/// fixture because none of them mutates the credential the gate itself consults.</para>
/// </summary>
public sealed class AdminApiKeyEndpointsTests
{
    private const string LegacyKey = "the-real-admin-key";

    /// <summary>
    /// A factory with the pre-#58 shared key seeded, so these tests authenticate the way an
    /// upgraded deployment does. That is deliberate rather than incidental: it means every
    /// assertion below is also evidence for AC4 (the legacy key keeps working with no configuration
    /// change) — the whole CRUD surface is being driven by it.
    /// </summary>
    private static async Task<ArbitarrWebApplicationFactory> CreateSeededFactoryAsync()
    {
        var factory = new ArbitarrWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = LegacyKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = LegacyKey;
            }
        });

        return factory;
    }

    private static HttpClient CreateKeyedClient(ArbitarrWebApplicationFactory factory, string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, key);
        return client;
    }

    private static async Task<(long Id, string Plaintext)> CreateKeyAsync(
        HttpClient client,
        string label,
        ApiKeyScope scope)
    {
        using var response = await client.PostAsJsonAsync(
            AdminApiKeyEndpoints.KeysRoute,
            new { label, scope = scope.ToString() });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        Assert.NotNull(created);
        Assert.NotNull(created!.Key.Id);

        return (created.Key.Id!.Value, created.PlaintextKey);
    }

    [Fact]
    public async Task Created_key_returns_its_plaintext_exactly_once_and_never_again()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (_, plaintext) = await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);
        Assert.False(string.IsNullOrWhiteSpace(plaintext));

        // AC: "key values are shown once and stored only as a hash". Asserted over the RAW list
        // body rather than the deserialized shape, so this fails even if a future field started
        // carrying the value — a typed assertion could only check fields that exist today.
        using var list = await client.GetAsync(AdminApiKeyEndpoints.KeysRoute);
        var body = await list.Content.ReadAsStringAsync();

        Assert.DoesNotContain(plaintext, body, StringComparison.Ordinal);

        // And the hash is not published either: publishing it would make the list response a
        // verifiable oracle for guessing keys offline.
        Assert.DoesNotContain(ApiKeyHasher.Hash(plaintext), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Created_keys_are_listed_with_their_label_scope_and_created_time()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);
        await CreateKeyAsync(client, "monitoring", ApiKeyScope.ReadOnly);

        var keys = await client.GetFromJsonAsync<List<ApiKeyResponse>>(AdminApiKeyEndpoints.KeysRoute);
        Assert.NotNull(keys);

        var sonarr = Assert.Single(keys!, k => k.Label == "sonarr");
        Assert.Equal(nameof(ApiKeyScope.Admin), sonarr.Scope);
        Assert.NotNull(sonarr.CreatedAt);
        Assert.Null(sonarr.RevokedAt);

        var monitoring = Assert.Single(keys!, k => k.Label == "monitoring");
        Assert.Equal(nameof(ApiKeyScope.ReadOnly), monitoring.Scope);
    }

    [Fact]
    public async Task The_legacy_environment_key_appears_in_the_list_as_unrevokable_full_scope()
    {
        // AC: "the existing env-var key continues to work and appears in the list". Invisible
        // authority is the state the issue exists to end — a credential that opens every door and
        // is not on the list of credentials is the one an operator forgets is still live.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var keys = await client.GetFromJsonAsync<List<ApiKeyResponse>>(AdminApiKeyEndpoints.KeysRoute);
        Assert.NotNull(keys);

        var legacy = Assert.Single(keys!, k => k.IsLegacy);
        Assert.Equal(DbAdminKeyResolver.LegacyKeyLabel, legacy.Label);
        Assert.Equal(nameof(ApiKeyScope.Admin), legacy.Scope);

        // No id: there is no row, so there is nothing to address and nothing to revoke. The UI
        // reads this to explain why the row has no revoke button rather than offering one that 404s.
        Assert.Null(legacy.Id);
    }

    [Fact]
    public async Task The_legacy_key_still_authenticates_every_admin_route_after_the_upgrade()
    {
        // AC4, stated directly rather than only implied by the other tests using it: an upgraded
        // deployment that changes NO configuration keeps working. This is the failure mode that
        // would break every homelab box on restart, so it gets its own named test.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var ping = await client.PostAsync("/api/admin/ping", content: null);
        Assert.Equal(HttpStatusCode.OK, ping.StatusCode);

        using var settings = await client.GetAsync("/api/admin/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    [Fact]
    public async Task A_created_key_authenticates_admin_routes_and_a_revoked_one_stops_immediately()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (id, plaintext) = await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);

        // A second admin-scope key, so the AC5 lockout refusal is not what this test measures. It is
        // NOT scaffolding to work around an inconvenient check: with only "sonarr" present the
        // revocation is legitimately refused (see Revoking_the_last_admin_scope_key_is_refused_with_
        // an_explanatory_message, which asserts exactly that, legacy key notwithstanding). Revoking
        // the last admin key and revoking a key while others remain are different operations, and
        // this test is about the second one — that a revoked key stops authenticating at once.
        await CreateKeyAsync(client, "keeper", ApiKeyScope.Admin);

        using var mintedClient = CreateKeyedClient(factory, plaintext);
        using var beforeRevoke = await mintedClient.PostAsync("/api/admin/ping", content: null);
        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);

        using var revoke = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // AC: "a revoked key is rejected immediately" — 401, because a revoked key is indistinguishable
        // from an unknown one by design.
        using var afterRevoke = await mintedClient.PostAsync("/api/admin/ping", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Revoking_one_key_does_not_affect_any_other()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (doomedId, doomedKey) = await CreateKeyAsync(client, "doomed", ApiKeyScope.Admin);
        var (_, keeperKey) = await CreateKeyAsync(client, "keeper", ApiKeyScope.Admin);

        using var revoke = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{doomedId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var doomedClient = CreateKeyedClient(factory, doomedKey);
        using var doomedResponse = await doomedClient.PostAsync("/api/admin/ping", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, doomedResponse.StatusCode);

        using var keeperClient = CreateKeyedClient(factory, keeperKey);
        using var keeperResponse = await keeperClient.PostAsync("/api/admin/ping", content: null);
        Assert.Equal(HttpStatusCode.OK, keeperResponse.StatusCode);

        // And the legacy key, which is a third credential entirely, is untouched.
        using var legacyResponse = await client.PostAsync("/api/admin/ping", content: null);
        Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
    }

    [Fact]
    public async Task Revoking_the_last_admin_scope_key_is_refused_with_an_explanatory_message()
    {
        // AC5. Note this holds even though the legacy key WOULD still admit the operator — the
        // refusal deliberately counts named keys only, so a deployment that has migrated off the
        // legacy key (the point of #58) is protected too. See ApiKeyRepository.RevokeAsync.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (id, _) = await CreateKeyAsync(client, "only-admin", ApiKeyScope.Admin);
        await CreateKeyAsync(client, "monitoring", ApiKeyScope.ReadOnly);

        using var response = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("last API key with admin scope", body, StringComparison.Ordinal);
        Assert.Contains("Create a replacement", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoking_an_unknown_key_is_a_404()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var response = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/424242");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_label_is_rejected_with_400()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);

        using var response = await client.PostAsJsonAsync(
            AdminApiKeyEndpoints.KeysRoute,
            new { label = "sonarr", scope = nameof(ApiKeyScope.Admin) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unrecognised_scope_is_rejected_with_400_naming_the_valid_values()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var response = await client.PostAsJsonAsync(
            AdminApiKeyEndpoints.KeysRoute,
            new { label = "confused", scope = "superuser" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(nameof(ApiKeyScope.ReadOnly), body, StringComparison.Ordinal);
        Assert.Contains(nameof(ApiKeyScope.Admin), body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]    // Admin's numeric form — the privilege escalation this closes
    [InlineData(" 1 ")]  // ...which Enum.TryParse also accepts padded
    [InlineData("+1")]   // ...and signed
    [InlineData("0")]    // ReadOnly's numeric form: no escalation, but still an undocumented shape
    [InlineData("5")]    // undefined ordinal, previously caught only downstream by Enum.IsDefined
    public async Task A_numeric_scope_string_is_rejected_with_400(string scope)
    {
        // Enum.TryParse ACCEPTS AN ENUM'S NUMERIC FORM, so parsing the scope with it let
        // {"scope":"1"} mint a full-authority Admin key — an input shape no caller is documented to
        // have and which this endpoint's own error message does not advertise. Neither Enum.IsDefined
        // (1 is defined) nor trimming (" 1 " and "+1" parse too) closes it; only matching the scope
        // names does, which is what the handler now does.
        //
        // "5" is here for a different reason: it was already refused, but downstream by the
        // repository's Enum.IsDefined rather than at the wire boundary. Pinning it stops a future
        // refactor moving that refusal somewhere it can be missed.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var response = await client.PostAsJsonAsync(
            AdminApiKeyEndpoints.KeysRoute,
            new { label = $"numeric-{scope.Trim()}", scope });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // And nothing was minted — the refusal must not be cosmetic.
        var keys = await client.GetFromJsonAsync<List<ApiKeyResponse>>(AdminApiKeyEndpoints.KeysRoute);
        Assert.DoesNotContain(keys!, k => k.Label.StartsWith("numeric-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_omitted_scope_defaults_to_the_narrower_one()
    {
        // A caller who forgets the field gets the LEAST authority. The opposite default would hand
        // full admin rights to a request that never asked for them.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var response = await client.PostAsJsonAsync(
            AdminApiKeyEndpoints.KeysRoute,
            new { label = "unspecified" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>();
        Assert.Equal(nameof(ApiKeyScope.ReadOnly), created!.Key.Scope);
    }

    [Fact]
    public async Task An_empty_label_is_rejected_with_400()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var response = await client.PostAsJsonAsync(
            AdminApiKeyEndpoints.KeysRoute,
            new { label = "   ", scope = nameof(ApiKeyScope.Admin) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_key_with_no_body_at_all_is_rejected_by_the_gate_first_not_by_binding()
    {
        // THE REQUIRED-BODY TRAP, asserted for this route specifically rather than relying on the
        // enumeration sweep (which covers the CONCRETE routes but is the same guard). A body bound
        // as REQUIRED is model-bound BEFORE endpoint filters run, so an unauthenticated remote
        // caller sending none would get 400 — proof the route exists — instead of the gate's answer.
        // Here the caller is unauthenticated AND sends no body: the gate must answer first.
        await using var factory = await CreateSeededFactoryAsync();
        using var unauthenticated = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, AdminApiKeyEndpoints.KeysRoute);
        using var response = await unauthenticated.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_request_with_no_body_is_a_400_from_the_handler()
    {
        // The other side of the same coin: past the gate, a missing body IS a 400 — but it is the
        // handler's null check saying so, not model binding short-circuiting the filter.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var request = new HttpRequestMessage(HttpMethod.Post, AdminApiKeyEndpoints.KeysRoute)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Last_used_is_null_until_the_key_authenticates_something()
    {
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        await CreateKeyAsync(client, "never-used", ApiKeyScope.Admin);

        var keys = await client.GetFromJsonAsync<List<ApiKeyResponse>>(AdminApiKeyEndpoints.KeysRoute);
        var unused = Assert.Single(keys!, k => k.Label == "never-used");

        Assert.Null(unused.LastUsedAt);
    }

    [Fact]
    public async Task Last_used_is_recorded_after_the_key_authenticates_a_request()
    {
        // AC: "last-used timestamp is recorded per key". The write is dispatched off the request
        // path by ThrottledApiKeyLastUsedRecorder, so this polls rather than asserting immediately —
        // asserting straight after the authenticated call would be racing the very detachment the
        // mechanism exists to provide, and a test that sometimes wins that race is worse than none.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (_, plaintext) = await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);

        using var mintedClient = CreateKeyedClient(factory, plaintext);
        using var used = await mintedClient.PostAsync("/api/admin/ping", content: null);
        Assert.Equal(HttpStatusCode.OK, used.StatusCode);

        DateTimeOffset? lastUsed = null;
        for (var attempt = 0; attempt < 50 && lastUsed is null; attempt++)
        {
            await Task.Delay(100);
            var keys = await client.GetFromJsonAsync<List<ApiKeyResponse>>(AdminApiKeyEndpoints.KeysRoute);
            lastUsed = Assert.Single(keys!, k => k.Label == "sonarr").LastUsedAt;
        }

        Assert.NotNull(lastUsed);
    }

    [Fact]
    public async Task The_keys_routes_are_admin_scoped_including_the_GET()
    {
        // The gate is by path prefix, never by verb — and these routes additionally take NO
        // ReadOnly relaxation, unlike /api/admin/search. Listing keys tells a caller which labels
        // exist and what authority each holds; that is a target list, not monitoring data.
        //
        // Named explicitly because the route-enumeration sweep does NOT cover the {id}-templated
        // DELETE, so its passing is not evidence about that route.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (id, _) = await CreateKeyAsync(client, "under-test", ApiKeyScope.Admin);
        var (_, readOnlyKey) = await CreateKeyAsync(client, "monitoring", ApiKeyScope.ReadOnly);

        using var readOnlyClient = CreateKeyedClient(factory, readOnlyKey);

        using var list = await readOnlyClient.GetAsync(AdminApiKeyEndpoints.KeysRoute);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        using var create = await readOnlyClient.PostAsJsonAsync(
            AdminApiKeyEndpoints.KeysRoute,
            new { label = "escalation", scope = nameof(ApiKeyScope.Admin) });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        using var revoke = await readOnlyClient.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
    }

    [Fact]
    public async Task No_response_from_any_keys_route_carries_a_stored_key_value()
    {
        // §5's "no stored key is ever returned by any endpoint", asserted against every response
        // shape this surface produces. The create response is excluded ON PURPOSE — it is the one
        // deliberate reveal, and the previous test asserts the value never reappears after it.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (id, plaintext) = await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);
        await CreateKeyAsync(client, "keeper", ApiKeyScope.Admin);

        using var list = await client.GetAsync(AdminApiKeyEndpoints.KeysRoute);
        Assert.DoesNotContain(plaintext, await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var revoke = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}");
        Assert.DoesNotContain(plaintext, await revoke.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // And the legacy key's own value never appears either, even though the resolver reads it to
        // decide the list entry should exist at all.
        using var listAgain = await client.GetAsync(AdminApiKeyEndpoints.KeysRoute);
        Assert.DoesNotContain(LegacyKey, await listAgain.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_never_appear_on_the_settings_surface()
    {
        // The property the source-key design established and this feature must preserve: keys are
        // not reachable through GET /api/admin/settings, which projects from SettingsCatalog.Entries.
        // ApiKeyEntry rows live in their own table and were never candidates for that projection —
        // this asserts it stays that way rather than trusting that it does.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (_, plaintext) = await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);

        using var settings = await client.GetAsync("/api/admin/settings");
        var body = await settings.Content.ReadAsStringAsync();

        Assert.DoesNotContain(plaintext, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKeyHasher.Hash(plaintext), body, StringComparison.Ordinal);
        Assert.DoesNotContain(LegacyKey, body, StringComparison.Ordinal);

        // And the response really is the settings catalog, not an error that trivially contains none
        // of the above.
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        using var parsed = JsonDocument.Parse(body);
        Assert.NotEmpty(parsed.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Removing_a_revoked_key_drops_only_that_row_from_the_list()
    {
        // #98 AC4 over the wire, PER ROW. The list is asserted by label after the removal, so a
        // handler that deleted a neighbour -- or every tombstone -- fails here rather than passing
        // on a bare count.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (liveId, _) = await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);
        var (doomedId, _) = await CreateKeyAsync(client, "retired-laptop", ApiKeyScope.ReadOnly);
        var (otherTombstoneId, _) = await CreateKeyAsync(client, "old-script", ApiKeyScope.ReadOnly);

        using var revokeDoomed = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{doomedId}");
        Assert.Equal(HttpStatusCode.NoContent, revokeDoomed.StatusCode);
        using var revokeOther = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{otherTombstoneId}");
        Assert.Equal(HttpStatusCode.NoContent, revokeOther.StatusCode);

        using var removed = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{doomedId}/tombstone");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var keys = await client.GetFromJsonAsync<List<ApiKeyResponse>>(AdminApiKeyEndpoints.KeysRoute);

        Assert.DoesNotContain(keys!, k => k.Id == doomedId);

        var live = Assert.Single(keys!, k => k.Id == liveId);
        Assert.Equal("sonarr", live.Label);
        Assert.Null(live.RevokedAt);

        var survivingTombstone = Assert.Single(keys!, k => k.Id == otherTombstoneId);
        Assert.Equal("old-script", survivingTombstone.Label);
        Assert.NotNull(survivingTombstone.RevokedAt);
    }

    [Fact]
    public async Task Removing_a_live_key_is_refused_with_the_servers_own_message()
    {
        // The refusal the UI renders verbatim. Asserted on the message body as well as the status,
        // because a 400 carrying a generic "Bad Request" would leave the operator no way to learn
        // that revoking first is what unblocks them.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        var (liveId, _) = await CreateKeyAsync(client, "sonarr", ApiKeyScope.Admin);

        using var refused = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{liveId}/tombstone");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("sonarr", body, StringComparison.Ordinal);
        Assert.Contains("Revoke it first", body, StringComparison.Ordinal);

        // And the key is still there, still live: the refusal is not a delete that also complained.
        var keys = await client.GetFromJsonAsync<List<ApiKeyResponse>>(AdminApiKeyEndpoints.KeysRoute);
        Assert.Null(Assert.Single(keys!, k => k.Id == liveId).RevokedAt);
    }

    [Fact]
    public async Task Removing_an_unknown_or_already_removed_id_answers_404()
    {
        // The deliberate 404-over-204 choice (see RemoveRevokedApiKeyAsync's doc comment): after a
        // hard delete an unknown id and an already-removed one are the same state, so both get the
        // same answer rather than a 204 that would claim success for any id at all. The repeat is
        // still a no-op, which is the idempotence that matters.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        using var unknown = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/424242/tombstone");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await CreateKeyAsync(client, "other-admin", ApiKeyScope.Admin);
        var (id, _) = await CreateKeyAsync(client, "retired", ApiKeyScope.ReadOnly);

        using var revoke = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var first = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}/tombstone");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using var second = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}/tombstone");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task The_remove_route_is_admin_gated_by_name_because_the_sweep_skips_it()
    {
        // CLAUDE.md section 2: the remove route is TEMPLATED, so AdminApiKeyRouteEnumerationTests
        // skips it and that sweep passing is NOT evidence about this route. Its gating is therefore
        // asserted here, by name, against all three callers -- and against the SAME status the
        // concrete sibling route answers, so a route that silently fell out of the admin prefix
        // classification cannot pass by answering something merely unhelpful.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        await CreateKeyAsync(client, "other-admin", ApiKeyScope.Admin);
        var (id, _) = await CreateKeyAsync(client, "under-test", ApiKeyScope.ReadOnly);
        var (_, readOnlyKey) = await CreateKeyAsync(client, "monitoring", ApiKeyScope.ReadOnly);

        using var revoke = await client.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var route = $"{AdminApiKeyEndpoints.KeysRoute}/{id}/tombstone";

        // No key at all: the gate answers, and with the same status the sibling revoke gives.
        using var unauthenticated = factory.CreateClient();
        using var noKey = await unauthenticated.DeleteAsync(route);
        using var siblingNoKey = await unauthenticated.DeleteAsync($"{AdminApiKeyEndpoints.KeysRoute}/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(siblingNoKey.StatusCode, noKey.StatusCode);

        // A read-only key: authenticated, but these routes take no ReadOnly relaxation.
        using var readOnlyClient = CreateKeyedClient(factory, readOnlyKey);
        using var readOnly = await readOnlyClient.DeleteAsync(route);
        Assert.Equal(HttpStatusCode.Forbidden, readOnly.StatusCode);

        // The admin key is allowed through -- the positive control, without which the two refusals
        // above would pass just as well against a route that rejected everybody or did not exist.
        using var allowed = await client.DeleteAsync(route);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task The_remove_route_takes_no_body_so_the_gate_answers_an_unauthenticated_caller_first()
    {
        // The REQUIRED-BODY TRAP for this route. It binds no body at all, so nothing is model-bound
        // ahead of AdminApiKeyFilter and an unauthenticated caller cannot tell a live id from an
        // unknown one by the status it gets back.
        await using var factory = await CreateSeededFactoryAsync();
        using var client = CreateKeyedClient(factory, LegacyKey);

        await CreateKeyAsync(client, "other-admin", ApiKeyScope.Admin);
        var (liveId, _) = await CreateKeyAsync(client, "still-live", ApiKeyScope.ReadOnly);

        using var unauthenticated = factory.CreateClient();

        using var againstLive = await unauthenticated.DeleteAsync(
            $"{AdminApiKeyEndpoints.KeysRoute}/{liveId}/tombstone");
        using var againstUnknown = await unauthenticated.DeleteAsync(
            $"{AdminApiKeyEndpoints.KeysRoute}/424242/tombstone");

        Assert.Equal(HttpStatusCode.Unauthorized, againstLive.StatusCode);
        // Identical, so nothing about which ids exist leaks from outside the gate. Keyed, these two
        // differ (400 vs 404) -- asserted above -- which is what makes this sameness meaningful.
        Assert.Equal(againstLive.StatusCode, againstUnknown.StatusCode);
    }
}
