using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Wave-C item 4 / R11: covers the admin rule-management surface end to end against the real Host.
/// Every route is admin-gated (D2); create/update reject an invalid (ReDoS-shaped or otherwise
/// unconstructible) regex and an over-cap rule count with 400 + reason before any row is persisted
/// (AC24 — reject, never clamp), mirroring <see cref="AdminSettingsEndpointsTests"/>'s fixture usage.
/// </summary>
public sealed class AdminRuleEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";
    private const string RulesRoute = "/api/admin/rules";

    // Classic catastrophic-backtracking shape. FilterRule's constructor tries NonBacktracking first
    // (which accepts this pattern, since it has no backreference/lookaround), so this alone would
    // NOT fail construction — the point of this test is that the rule still evaluates safely inside
    // MatchTimeout when actually run, not that construction itself fails.
    private const string CatastrophicPattern = "(a+)+$";

    // A pattern NonBacktracking rejects AND that is invalid regex syntax, forcing FilterRule's
    // constructor itself to throw (unbalanced group) — this is what "rejected before save" exercises
    // for the create endpoint.
    private const string InvalidRegexPattern = "(unbalanced";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminRuleEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GET_rules_requires_the_admin_key()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(RulesRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_rules_without_admin_key_is_rejected_with_401()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            RulesRoute,
            new UpsertFilterRuleRequest("no-auth-rule", true, "no-auth-pattern", 2, true));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task POST_rules_with_admin_key_creates_a_valid_rule()
    {
        await SeedAdminKeyAsync();
        using var client = AuthorizedClient();

        var response = await client.PostAsJsonAsync(
            RulesRoute,
            new UpsertFilterRuleRequest("valid-rule", true, "1080p", 2, true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<FilterRuleResponse>();
        Assert.NotNull(created);
        Assert.Equal("valid-rule", created!.Name);
        Assert.True(created.Id > 0);

        var getResponse = await client.GetAsync(RulesRoute);
        var rules = await getResponse.Content.ReadFromJsonAsync<List<FilterRuleResponse>>();
        Assert.Contains(rules!, r => r.Id == created.Id && r.Pattern == "1080p");
    }

    [Fact]
    public async Task POST_rules_rejects_an_invalid_regex_pattern_with_400_before_saving()
    {
        await SeedAdminKeyAsync();
        using var client = AuthorizedClient();

        var beforeResponse = await client.GetAsync(RulesRoute);
        var before = await beforeResponse.Content.ReadFromJsonAsync<List<FilterRuleResponse>>();
        var beforeCount = before!.Count;

        var response = await client.PostAsJsonAsync(
            RulesRoute,
            new UpsertFilterRuleRequest("bad-regex-rule", true, InvalidRegexPattern, 2, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("error"));
        Assert.False(string.IsNullOrWhiteSpace(body["error"]));

        var afterResponse = await client.GetAsync(RulesRoute);
        var after = await afterResponse.Content.ReadFromJsonAsync<List<FilterRuleResponse>>();
        Assert.Equal(beforeCount, after!.Count);
    }

    [Fact]
    public async Task POST_rules_test_dry_runs_a_catastrophic_pattern_safely_without_persisting()
    {
        await SeedAdminKeyAsync();
        using var client = AuthorizedClient();

        var beforeResponse = await client.GetAsync(RulesRoute);
        var before = await beforeResponse.Content.ReadFromJsonAsync<List<FilterRuleResponse>>();
        var beforeCount = before!.Count;

        var response = await client.PostAsJsonAsync(
            "/api/admin/rules/test",
            new TestFilterRuleRequest("catastrophic-rule", true, CatastrophicPattern, 2, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaX"));

        // R11: FilterRule.MatchTimeout bounds the match itself, so this must complete promptly with
        // some verdict (Unknown on timeout, or a real match) rather than hanging the request.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<TestFilterRuleResponse>();
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.Verdict));

        var afterResponse = await client.GetAsync(RulesRoute);
        var after = await afterResponse.Content.ReadFromJsonAsync<List<FilterRuleResponse>>();
        Assert.Equal(beforeCount, after!.Count);
    }

    [Fact]
    public async Task POST_rules_test_rejects_an_invalid_regex_with_400()
    {
        await SeedAdminKeyAsync();
        using var client = AuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/api/admin/rules/test",
            new TestFilterRuleRequest("bad-regex-test", true, InvalidRegexPattern, 2, "some title"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PUT_rules_updates_an_existing_rule()
    {
        await SeedAdminKeyAsync();
        using var client = AuthorizedClient();

        var createResponse = await client.PostAsJsonAsync(
            RulesRoute,
            new UpsertFilterRuleRequest("update-me", true, "before-pattern", 2, true));
        var created = await createResponse.Content.ReadFromJsonAsync<FilterRuleResponse>();

        var updateResponse = await client.PutAsJsonAsync(
            $"{RulesRoute}/{created!.Id}",
            new UpsertFilterRuleRequest("update-me", false, "after-pattern", 3, false));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<FilterRuleResponse>();
        Assert.Equal("after-pattern", updated!.Pattern);
        Assert.False(updated.IsAllow);
        Assert.Equal(3, updated.Precedence);
        Assert.False(updated.Enabled);
    }

    [Fact]
    public async Task DELETE_rules_removes_an_existing_rule()
    {
        await SeedAdminKeyAsync();
        using var client = AuthorizedClient();

        var createResponse = await client.PostAsJsonAsync(
            RulesRoute,
            new UpsertFilterRuleRequest("delete-me", true, "delete-me-pattern", 2, true));
        var created = await createResponse.Content.ReadFromJsonAsync<FilterRuleResponse>();

        var deleteResponse = await client.DeleteAsync($"{RulesRoute}/{created!.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync(RulesRoute);
        var rules = await getResponse.Content.ReadFromJsonAsync<List<FilterRuleResponse>>();
        Assert.DoesNotContain(rules!, r => r.Id == created.Id);
    }

    [Fact]
    public async Task POST_rules_rejects_the_501st_rule_in_a_profile_with_400()
    {
        const string ProfileName = "rule-count-cap-profile";
        string? previousDefaultProfileName = null;
        await SeedAdminKeyAsync();

        // Seed a dedicated profile flagged as default with exactly MaxRulesPerProfile (500) rules
        // pre-populated directly against the DB (bypassing the endpoint, which is what's under test),
        // so the 501st attempt through the endpoint is the one that must be rejected.
        await _factory.SeedAsync(async db =>
        {
            // AdminRuleEndpoints.DefaultProfileIdAsync creates a "Default" IsDefault profile lazily
            // if none exists; any prior test in this shared fixture may have already created one, so
            // reuse/promote a single IsDefault profile deterministically here. Remember its name so
            // it can be restored as the default once this test is done (see cleanup below) — this
            // class shares one database across all its tests via IClassFixture.
            var existingDefault = db.FilterProfiles.FirstOrDefault(p => p.IsDefault);
            if (existingDefault is not null)
            {
                previousDefaultProfileName = existingDefault.Name;
                existingDefault.IsDefault = false;
            }

            var profile = new FilterProfileEntry
            {
                Name = ProfileName,
                IsDefault = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.FilterProfiles.Add(profile);
            await db.SaveChangesAsync();

            for (var i = 0; i < SettingsValidator.MaxRulesPerProfile; i++)
            {
                db.FilterRules.Add(new FilterRuleEntry
                {
                    FilterProfileId = profile.Id,
                    Name = $"cap-rule-{i}",
                    IsAllow = true,
                    Pattern = $"cap-pattern-{i}",
                    Precedence = 2,
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
        });

        using var client = AuthorizedClient();

        var response = await client.PostAsJsonAsync(
            RulesRoute,
            new UpsertFilterRuleRequest("one-too-many", true, "one-too-many-pattern", 2, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Contains("500", body!["error"]);

        var getResponse = await client.GetAsync(RulesRoute);
        var rules = await getResponse.Content.ReadFromJsonAsync<List<FilterRuleResponse>>();
        Assert.Equal(SettingsValidator.MaxRulesPerProfile, rules!.Count);
        Assert.DoesNotContain(rules, r => r.Name == "one-too-many");

        // This class shares one SQLite database across all tests via IClassFixture, and xUnit does
        // not guarantee method execution order within a class. Undo the profile-swap and delete the
        // 500 seeded rows here so a test that runs after this one still sees whatever default profile
        // (and rule count) existed before this test ran, rather than this test's cap-profile leaking
        // into unrelated tests' assertions.
        await _factory.SeedAsync(async db =>
        {
            var capProfile = db.FilterProfiles.FirstOrDefault(p => p.Name == ProfileName);
            if (capProfile is not null)
            {
                var capRules = db.FilterRules.Where(r => r.FilterProfileId == capProfile.Id);
                db.FilterRules.RemoveRange(capRules);
                db.FilterProfiles.Remove(capProfile);
            }

            if (previousDefaultProfileName is not null)
            {
                var restoredDefault = db.FilterProfiles.FirstOrDefault(p => p.Name == previousDefaultProfileName);
                if (restoredDefault is not null)
                {
                    restoredDefault.IsDefault = true;
                }
            }

            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("/admin-rules.html")]
    [InlineData("/admin-rules.js")]
    public async Task Admin_rules_static_assets_are_served(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
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
