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

    /// <summary>
    /// arb-x7w8.1 POSITIVE CONTROL for the leak sweep above (CLAUDE.md §4). The sweep asserts the
    /// key is absent from every response body, and this proves that assertion is CAPABLE OF FAILING
    /// — that it would catch a key projected onto <see cref="SourceResponse"/> rather than passing
    /// vacuously because the key was never in play.
    ///
    /// <para>Asserting that the fixture was created (a 201, a true <c>HasApiKey</c>) proves the
    /// secret EXISTS. It does not prove it would be DETECTABLE if it leaked. So this plants the
    /// exact secret into a response body by the only route that can carry one — serializing the
    /// response the endpoint really produced, with the key attached to it — and asserts the very
    /// same <c>DoesNotContain</c> check the sweep uses FAILS against it. Only then does it assert
    /// the real body passes. Without this first half, a rename of the constant, an empty body, or a
    /// serializer that dropped everything would leave the sweep green and silent.</para>
    ///
    /// <para>This is the shape <c>LogSecretInjectionTests</c> uses — it asserts the cleanser's
    /// replacement marker IS PRESENT, proving the secret reached the check and was scrubbed, rather
    /// than merely never arriving. The three shipped regressions this guards against (#57, #80, #78)
    /// all passed a bare absence assertion while a real leak was live.</para>
    /// </summary>
    [Fact]
    public async Task The_no_key_in_response_assertion_would_fail_if_a_key_were_projected()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var created = await CreateSourceAsync(client, "Control probe " + Guid.NewGuid().ToString("N"), SecretApiKey);
        Assert.True(created.HasApiKey);

        using var listResponse = await client.GetAsync(SourcesRoute);
        var realBody = await listResponse.Content.ReadAsStringAsync();

        // The mutant: the real response with the key projected onto it, exactly as a SourceResponse
        // that had gained a key-carrying property would serialize. Nothing vulnerable is added to
        // the product — this constructs the leaked shape here, in the test, and throws it away.
        var leakedBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = created.Id,
            kind = created.Kind,
            displayName = created.DisplayName,
            baseUrl = created.BaseUrl,
            enabled = created.Enabled,
            hasApiKey = created.HasApiKey,
            apiKey = SecretApiKey,
            apiPath = created.ApiPath,
            priority = created.Priority,
            limitsUnit = created.LimitsUnit,
            nzbAccessMode = created.NzbAccessMode,
        });

        // FIRST: prove the check bites. If this does not throw, the assertion used by the sweep is
        // incapable of detecting a leak and every "no key in the body" test in this file is vacuous.
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => Assert.DoesNotContain(SecretApiKey, leakedBody, StringComparison.Ordinal));

        // THEN: the real body, checked by that now-proven-capable assertion, carries no key.
        Assert.DoesNotContain(SecretApiKey, realBody, StringComparison.Ordinal);

        // And the response really did describe the source holding the secret, so the check above ran
        // against the right payload rather than an empty or unrelated one.
        Assert.Contains(created.DisplayName, realBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-x7w8.1: the two new kinds are accepted end to end and round-trip under their own names,
    /// asserted per kind. <see cref="SourceRepository.NzbHydraKind"/> is covered by the existing
    /// create test above, so all three kinds are pinned.
    /// </summary>
    [Theory]
    [InlineData(SourceRepository.NewznabKind)]
    [InlineData(SourceRepository.TorznabKind)]
    public async Task A_source_of_each_new_kind_can_be_created_and_reads_back_with_that_kind(string kind)
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind,
            displayName = $"{kind} indexer " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.33:9117",
            apiKey = SecretApiKey,
            enabled = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<SourceResponse>();
        Assert.NotNull(created);
        Assert.Equal(kind, created!.Kind);

        // The documented defaults land rather than zero/empty — notably Proxy, the mode that does
        // NOT expose the indexer key to the client.
        Assert.Equal("/api", created.ApiPath);
        Assert.Equal("Day", created.LimitsUnit);
        Assert.Equal("Proxy", created.NzbAccessMode);
    }

    /// <summary>
    /// arb-x7w8.1, per casing variant. The existing arb-pn5 theory covers the NzbHydra spellings;
    /// this covers the two kinds added here, and asserts each variant separately rather than "some
    /// variant is rejected", so a fix that special-cased one spelling cannot pass.
    /// </summary>
    [Theory]
    [InlineData("newznab")]
    [InlineData("NEWZNAB")]
    [InlineData("torznab")]
    [InlineData("TORZNAB")]
    public async Task A_wrongly_cased_new_source_kind_is_rejected_with_400(string wrongCasing)
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = wrongCasing,
            displayName = "Bad new casing " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.34:9117",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Newznab", body, StringComparison.Ordinal);
        Assert.Contains("Torznab", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The null-is-not-zero distinction survives the wire, not merely the column: a source created
    /// with an explicit <c>0</c> limit and one created with no limit at all must come back as two
    /// different values. Asserted per source and then against each other, so a projection that
    /// collapsed either into the other is caught in both directions.
    /// </summary>
    [Fact]
    public async Task Null_and_zero_limits_survive_the_wire_as_distinct_values()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var unlimitedResponse = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = SourceRepository.NewznabKind,
            displayName = "Unlimited " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.35:9117",
            queryLimit = (int?)null,
            grabLimit = (int?)null,
        });
        Assert.Equal(HttpStatusCode.Created, unlimitedResponse.StatusCode);
        var unlimited = await unlimitedResponse.Content.ReadFromJsonAsync<SourceResponse>();

        using var cappedResponse = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = SourceRepository.NewznabKind,
            displayName = "Zero capped " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.36:9117",
            queryLimit = 0,
            grabLimit = 0,
        });
        Assert.Equal(HttpStatusCode.Created, cappedResponse.StatusCode);
        var capped = await cappedResponse.Content.ReadFromJsonAsync<SourceResponse>();

        Assert.NotNull(unlimited);
        Assert.NotNull(capped);

        Assert.Null(unlimited!.QueryLimit);
        Assert.Null(unlimited.GrabLimit);
        Assert.Equal(0, capped!.QueryLimit);
        Assert.Equal(0, capped.GrabLimit);

        Assert.NotEqual(unlimited.QueryLimit, capped.QueryLimit);
        Assert.NotEqual(unlimited.GrabLimit, capped.GrabLimit);
    }

    [Theory]
    [InlineData("limitsUnit", "day")]
    [InlineData("limitsUnit", "Week")]
    [InlineData("nzbAccessMode", "redirect")]
    [InlineData("nzbAccessMode", "Passthrough")]
    // The numeric forms CLAUDE.md §3 names: Enum.TryParse would mint the second member of a
    // two-value set from "1", and neither Enum.IsDefined nor trimming closes that ("1" IS defined,
    // "+1" parses). Matching by exact name is what closes it; these pin that it stays closed.
    [InlineData("limitsUnit", "1")]
    [InlineData("limitsUnit", "+1")]
    [InlineData("nzbAccessMode", "1")]
    [InlineData("nzbAccessMode", "+1")]
    public async Task A_closed_set_column_outside_its_known_values_is_rejected_with_400(string field, string value)
    {
        // CLAUDE.md §3 at the wire boundary, per field and per value. nzbAccessMode matters most:
        // it selects whether the indexer key is exposed to the client.
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var body = new Dictionary<string, object?>
        {
            ["kind"] = SourceRepository.NewznabKind,
            ["displayName"] = "Bad closed set " + Guid.NewGuid().ToString("N"),
            ["baseUrl"] = "http://192.0.2.37:9117",
            [field] = value,
        };

        using var response = await client.PostAsJsonAsync(SourcesRoute, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// arb-x7w8.14's ship-OFF ruling, enforced at the wire. The correctly-spelled <c>"Redirect"</c>
    /// is refused on BOTH write paths, so no request shape produces a source that exposes its key to
    /// the client. When arb-x7w8.14 lands the Settings UI warning, this test changes deliberately.
    /// </summary>
    [Fact]
    public async Task Setting_the_redirect_access_mode_is_rejected_with_400_on_both_write_paths()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var createResponse = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = SourceRepository.NewznabKind,
            displayName = "Redirect create " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.39:9117",
            nzbAccessMode = SourceRepository.RedirectAccessMode,
        });
        Assert.Equal(HttpStatusCode.BadRequest, createResponse.StatusCode);

        // And a legitimately-created Proxy source cannot be rewritten to Redirect.
        var created = await CreateSourceAsync(client, "Redirect update " + Guid.NewGuid().ToString("N"), SecretApiKey);
        Assert.Equal(SourceRepository.ProxyAccessMode, created.NzbAccessMode);

        using var updateResponse = await client.PutAsJsonAsync($"{SourcesRoute}/{created.Id}", new
        {
            kind = created.Kind,
            displayName = created.DisplayName,
            baseUrl = created.BaseUrl,
            enabled = true,
            nzbAccessMode = SourceRepository.RedirectAccessMode,
        });
        Assert.Equal(HttpStatusCode.BadRequest, updateResponse.StatusCode);

        // The stored source is still Proxy — the rejected update changed nothing.
        using var readBack = await client.GetAsync(SourcesRoute);
        var sources = await readBack.Content.ReadFromJsonAsync<List<SourceResponse>>();
        Assert.Equal(
            SourceRepository.ProxyAccessMode,
            sources!.Single(s => s.Id == created.Id).NzbAccessMode);
    }

    /// <summary>
    /// The update path's "no opinion" contract over the wire: a PUT that omits the tuning fields
    /// must leave every one of them as stored. This is precisely the body the EXISTING Sources UI
    /// sends — it knows nothing about these columns — so an implementation that reset them on every
    /// update would silently wipe an operator's tuning the next time they renamed a source.
    /// </summary>
    [Fact]
    public async Task A_put_omitting_the_tuning_fields_preserves_them()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var name = "Tuned over the wire " + Guid.NewGuid().ToString("N");
        using var createResponse = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = SourceRepository.NewznabKind,
            displayName = name,
            baseUrl = "http://192.0.2.40:9117",
            apiPath = "/api/v2.0/indexers/example/results/torznab",
            priority = 15,
            timeoutSeconds = 60,
            queryLimit = 200,
            grabLimit = 20,
            limitsUnit = "Hour",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<SourceResponse>();

        // The body the current UI sends: identity fields only, no tuning fields at all.
        using var updateResponse = await client.PutAsJsonAsync($"{SourcesRoute}/{created!.Id}", new
        {
            kind = created.Kind,
            displayName = created.DisplayName,
            baseUrl = created.BaseUrl,
            enabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        var updated = await updateResponse.Content.ReadFromJsonAsync<SourceResponse>();
        Assert.NotNull(updated);

        // The field the PUT DID carry changed...
        Assert.False(updated!.Enabled);

        // ...and every tuning field it did not carry is untouched, asserted per field.
        Assert.Equal("/api/v2.0/indexers/example/results/torznab", updated.ApiPath);
        Assert.Equal(15, updated.Priority);
        Assert.Equal(60, updated.TimeoutSeconds);
        Assert.Equal(200, updated.QueryLimit);
        Assert.Equal(20, updated.GrabLimit);
        Assert.Equal("Hour", updated.LimitsUnit);
        Assert.Equal(SourceRepository.ProxyAccessMode, updated.NzbAccessMode);
    }

    /// <summary>
    /// The limits' Clear affordance over the wire, and the reason it cannot be expressed by sending
    /// <c>null</c>: JSON gives the handler no way to tell an omitted field from an explicit null, so
    /// without the flag "set this back to unlimited" and "leave it alone" arrive identically.
    ///
    /// <para>Asserted against the OTHER two outcomes in the same test — a cleared limit, a zeroed
    /// limit and an untouched limit must be three different stored values. Dropping the
    /// <c>ClearQueryLimit ||</c> term from the handler makes the clear silently behave as
    /// "leave alone", which only a test that distinguishes those two can catch.</para>
    /// </summary>
    [Fact]
    public async Task Clearing_a_limit_over_the_wire_differs_from_zeroing_it_and_from_omitting_it()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        async Task<SourceResponse> CappedAsync(string label)
        {
            using var response = await client.PostAsJsonAsync(SourcesRoute, new
            {
                kind = SourceRepository.NewznabKind,
                displayName = $"{label} " + Guid.NewGuid().ToString("N"),
                baseUrl = "http://192.0.2.42:9117",
                queryLimit = 500,
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = await response.Content.ReadFromJsonAsync<SourceResponse>();
            Assert.Equal(500, created!.QueryLimit);
            return created;
        }

        async Task<SourceResponse> PutAsync(SourceResponse s, object body)
        {
            using var response = await client.PutAsJsonAsync($"{SourcesRoute}/{s.Id}", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SourceResponse>())!;
        }

        var toClear = await CappedAsync("Wire cleared");
        var toZero = await CappedAsync("Wire zeroed");
        var toLeave = await CappedAsync("Wire untouched");

        var cleared = await PutAsync(toClear, new
        {
            kind = toClear.Kind,
            displayName = toClear.DisplayName,
            baseUrl = toClear.BaseUrl,
            enabled = true,
            clearQueryLimit = true,
        });

        var zeroed = await PutAsync(toZero, new
        {
            kind = toZero.Kind,
            displayName = toZero.DisplayName,
            baseUrl = toZero.BaseUrl,
            enabled = true,
            queryLimit = 0,
        });

        var left = await PutAsync(toLeave, new
        {
            kind = toLeave.Kind,
            displayName = toLeave.DisplayName,
            baseUrl = toLeave.BaseUrl,
            enabled = true,
        });

        Assert.Null(cleared.QueryLimit);
        Assert.Equal(0, zeroed.QueryLimit);
        Assert.Equal(500, left.QueryLimit);

        // Pairwise distinct: no outcome collapsed into another.
        Assert.NotEqual(cleared.QueryLimit, zeroed.QueryLimit);
        Assert.NotEqual(cleared.QueryLimit, left.QueryLimit);
        Assert.NotEqual(zeroed.QueryLimit, left.QueryLimit);
    }

    /// <summary>
    /// The timeout's Clear affordance over the wire: null means "fall back to the global default",
    /// so an operator who set an override must be able to return to it. Omitting the field cannot
    /// express that — omission means "leave alone" — which is why the explicit flag exists.
    /// </summary>
    [Fact]
    public async Task A_timeout_override_can_be_set_and_then_cleared_back_to_null()
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var name = "Clearable timeout " + Guid.NewGuid().ToString("N");
        using var createResponse = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = SourceRepository.NewznabKind,
            displayName = name,
            baseUrl = "http://192.0.2.41:9117",
            timeoutSeconds = 30,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<SourceResponse>();
        Assert.Equal(30, created!.TimeoutSeconds);

        using var clearResponse = await client.PutAsJsonAsync($"{SourcesRoute}/{created.Id}", new
        {
            kind = created.Kind,
            displayName = created.DisplayName,
            baseUrl = created.BaseUrl,
            enabled = true,
            clearTimeoutSeconds = true,
        });
        Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);

        var cleared = await clearResponse.Content.ReadFromJsonAsync<SourceResponse>();
        Assert.Null(cleared!.TimeoutSeconds);
    }

    [Fact]
    public async Task An_api_path_embedding_a_key_or_a_query_string_is_rejected_with_400()
    {
        // A key pasted into apiPath would be a second, READABLE home for a secret designed to live
        // write-only in a Settings row — and it would ride into every backup and every response.
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = SourceRepository.NewznabKind,
            displayName = "Key in path " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.38:9117",
            apiPath = $"/api?apikey={SecretApiKey}",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The rejection message must not echo the submitted key back to the caller.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SecretApiKey, body, StringComparison.Ordinal);
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
        // reads the actual stored row's value BEFORE the update (proving the seeded key really
        // reached the row) and AGAIN afterward (proving the omitted-apiKey PUT left that exact
        // value in place), rather than trusting the boolean or a single post-update read that
        // could not tell "preserved" from "coincidentally re-seeded" (CLAUDE.md §4).
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var created = await CreateSourceAsync(client, "Key-preserving " + Guid.NewGuid().ToString("N"), SecretApiKey);

        await _factory.SeedAsync(db =>
        {
            var row = db.Settings.Find(SourceRepository.ApiKeySettingName(created.Id));
            Assert.NotNull(row);
            Assert.Equal(SecretApiKey, row!.Value);
            return Task.CompletedTask;
        });

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

    /// <summary>
    /// arb-pn5. Before this fix, a source created with <c>kind: "nzbhydra"</c> was stored and listed
    /// successfully — <see cref="Creating_a_source_persists_it_and_returns_201"/> proves the correctly
    /// cased request succeeds, so this is not a URL/display-name rejection wearing a kind label. The
    /// bug was silent: <c>SourceSeeder</c>'s ordinal <c>s.Kind == NzbHydraKind</c> comparison never
    /// matched a wrongly-cased row, so it was never resolved into the search pipeline and nothing
    /// ever reported an error. Both wrong casings are covered, not just lowercase, because a
    /// case-insensitive fix (rather than exact-match rejection) would make this pass while still
    /// leaving <c>SourceSeeder</c>'s comparison the one true authority for what actually resolves.
    /// </summary>
    [Theory]
    [InlineData("nzbhydra")]
    [InlineData("NZBHYDRA")]
    public async Task A_wrongly_cased_source_kind_is_rejected_with_400(string wrongCasing)
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = wrongCasing,
            displayName = "Bad casing " + Guid.NewGuid().ToString("N"),
            baseUrl = "http://192.0.2.32:5076",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The message names the accepted value, per the brief -- an operator hitting this should not
        // have to go read source code to learn the one spelling that works.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NzbHydra", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-5da. The theory above drives POST only, but <c>SourceRepository.UpdateAsync</c> calls the
    /// same <c>ValidateKind</c> as <c>CreateAsync</c> does, and nothing in the endpoint layer would
    /// notice if one of those two call sites were dropped. Without this case, deleting the
    /// <c>ValidateKind(kind)</c> line from the update path leaves the whole suite green while a
    /// correctly-cased row can be rewritten to a kind <c>SourceSeeder</c>'s ordinal comparison never
    /// matches — the exact silent-never-resolved failure arb-pn5 fixed, reachable again through PUT.
    ///
    /// <para>The row is created correctly cased first, so a rejection here can only be the kind on
    /// the update: the display name and URL are the same shapes the successful create just used.</para>
    /// </summary>
    [Theory]
    [InlineData("nzbhydra")]
    [InlineData("NZBHYDRA")]
    public async Task Updating_a_source_to_a_wrongly_cased_kind_is_rejected_with_400(string wrongCasing)
    {
        await SeedAdminKeyAsync();
        using var client = CreateAdminClient();

        var created = await CreateSourceAsync(client, "Recasable " + Guid.NewGuid().ToString("N"), SecretApiKey);
        Assert.Equal(SourceRepository.NzbHydraKind, created.Kind);

        using var response = await client.PutAsJsonAsync($"{SourcesRoute}/{created.Id}", new
        {
            kind = wrongCasing,
            displayName = created.DisplayName,
            baseUrl = created.BaseUrl,
            enabled = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("NzbHydra", body, StringComparison.Ordinal);
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
