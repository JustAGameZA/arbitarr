using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #65 plan §5: <c>GET /api/admin/logs</c> is gated per its classification, NAMED EXPLICITLY here.
///
/// Naming it matters even though it is a concrete (non-templated) route that
/// <see cref="AdminApiKeyRouteEnumerationTests"/>'s sweep does cover: that sweep asserts the generic
/// contract, while the decision that THIS route is gated at all is a deliberate one worth a test
/// that fails by name if someone reclassifies it. See <see cref="LogsEndpoint"/>'s remarks for why
/// raw application logs are gated while <c>/api/activity</c> is PublicRead — that asymmetry is
/// intentional and is exactly the kind of thing a later reader might "tidy up".
/// </summary>
public sealed class LogsEndpointTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public LogsEndpointTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_logs_route_rejects_a_request_without_the_admin_key()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/admin/logs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_logs_route_rejects_a_wrong_admin_key()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/logs");
        request.Headers.Add(AdminApiKeyFilter.HeaderName, "not-the-key");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_logs_route_serves_with_the_correct_admin_key()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        var response = await GetLogsAsync(client, "/api/admin/logs");

        Assert.NotNull(response);
    }

    [Fact]
    public async Task Filtering_by_level_and_paging_are_applied()
    {
        await SeedAdminKeyAsync();
        var store = _factory.Services.GetRequiredService<LogStore>();

        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.WriteAsync(Enumerable.Range(0, 4)
            .Select(i => new PendingLogEntry(
                start.AddSeconds(i), "Error", "Test.LogsEndpoint", $"endpoint probe {i}", null, null))
            .ToList());

        using var client = _factory.CreateClient();

        var page = await GetLogsAsync(client, "/api/admin/logs?level=Error&logger=Test.LogsEndpoint&page=1&pageSize=2");

        Assert.NotNull(page);
        Assert.Equal(2, page!.Entries.Count);
        Assert.Equal(4, page.Total);
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.All(page.Entries, e => Assert.Equal("Error", e.Level));

        var second = await GetLogsAsync(client, "/api/admin/logs?level=Error&logger=Test.LogsEndpoint&page=2&pageSize=2");
        Assert.Equal(2, second!.Entries.Count);
        // Newest-first ordering means page 2 holds strictly older ids than page 1 — and no overlap,
        // which is the property that actually breaks if the ordering key stops being unique.
        Assert.Empty(page.Entries.Select(e => e.Id).Intersect(second.Entries.Select(e => e.Id)));
    }

    [Fact]
    public async Task A_filter_matching_nothing_returns_an_empty_page_rather_than_an_error()
    {
        // The UI renders its "nothing here yet, and here is what would fill it" empty state from
        // this shape (#52). A 404 or a 500 here would put an error affordance in front of the
        // operator for the entirely ordinary case of "no Critical entries".
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        var page = await GetLogsAsync(client, "/api/admin/logs?logger=no.such.logger.exists");

        Assert.NotNull(page);
        Assert.Empty(page!.Entries);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task The_level_parameter_serves_that_severity_and_above()
    {
        // arb-pw7r, over the wire rather than only at the store: this is what makes the System >
        // Logs default of "Warning and above" show Error and Critical instead of hiding them.
        await SeedAdminKeyAsync();
        var store = _factory.Services.GetRequiredService<LogStore>();

        // Scoped to its own logger because the fixture's store is shared with the other tests in
        // this class -- an unscoped level query would also count their rows and the assertion
        // would depend on execution order.
        // Deliberately NOT prefixed with the other tests' "Test.LogsEndpoint": the logger filter is
        // a SUBSTRING match, so a name extending theirs would fold these rows into their result and
        // fail their totals.
        const string logger = "Test.MinLevelFilter";
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.WriteAsync(new[]
        {
            new PendingLogEntry(start, "Information", logger, "an info", null, null),
            new PendingLogEntry(start.AddSeconds(1), "Warning", logger, "a warning", null, null),
            new PendingLogEntry(start.AddSeconds(2), "Error", logger, "an error", null, null),
            new PendingLogEntry(start.AddSeconds(3), "Critical", logger, "a critical", null, null),
        });

        using var client = _factory.CreateClient();

        var page = await GetLogsAsync(client, $"/api/admin/logs?level=Warning&logger={logger}");

        Assert.NotNull(page);
        Assert.Equal(3, page!.Total);
        Assert.Equal(new[] { "Critical", "Error", "Warning" }, page.Entries.Select(e => e.Level));
        // The Information row is the positive control: it proves a row BELOW the requested level
        // was present to be excluded, without which its absence would assert nothing.
        Assert.DoesNotContain("an info", page.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task An_oversized_page_size_is_clamped()
    {
        await SeedAdminKeyAsync();
        using var client = _factory.CreateClient();

        var page = await GetLogsAsync(client, "/api/admin/logs?pageSize=100000");

        Assert.NotNull(page);
        Assert.Equal(LogStore.MaxPageSize, page!.PageSize);
    }

    private async Task<LogsResponse?> GetLogsAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LogsResponse>();
    }

    // Upsert, matching AdminApiKeyRouteEnumerationTests: the factory's SQLite database is shared
    // across every [Fact] in this IClassFixture-scoped class.
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
