using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Arbitarr.Core.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-ln0: <c>/api/status</c>'s health block, end to end against the real Host.
///
/// <para>This class owns its own <see cref="ArbitarrWebApplicationFactory"/> (constructed here, not
/// an <c>IClassFixture</c> shared with <see cref="DashboardReadOnlyTests"/>) precisely so the
/// "a fresh process reports no health items" assertion is honest: on a shared host another test
/// could have recorded a refusal first, and the absence assertion would then be testing nothing but
/// execution order.</para>
///
/// <para>The tracker is reached through the host's own DI container rather than through a seam, so
/// these tests exercise the same singleton the download proxy writes to and the status endpoint
/// reads — a registration that silently went missing would fail here.</para>
/// </summary>
public sealed class StatusHealthItemsTests : IDisposable
{
    private readonly ArbitarrWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_fresh_process_reports_no_health_items()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetFromJsonAsync<StatusResponse>("/api/status");

        Assert.NotNull(response);
        Assert.NotNull(response!.Health);
        Assert.Empty(response.Health);
    }

    [Fact]
    public async Task A_refused_download_surfaces_as_a_blocking_health_item_naming_the_source()
    {
        using var client = _factory.CreateClient();
        var tracker = _factory.Services.GetRequiredService<IDownloadRefusalTracker>();
        var at = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

        // Positive control for the absence test above: this proves a health item WOULD be detected
        // on this payload if one existed, so `Assert.Empty` there is not vacuously satisfied by a
        // Health property the endpoint never populates at all.
        await tracker.RecordRefusalAsync("nzbhydra2", "Refused HTTP 302: the source redirected instead of serving the file.", at);

        var response = await client.GetFromJsonAsync<StatusResponse>("/api/status");

        var item = Assert.Single(response!.Health);
        Assert.Equal(StatusEndpoint.DownloadRefusedRedirectKey, item.Key);
        Assert.Equal("blocking", item.Severity);
        Assert.Equal("nzbhydra2", item.SourceName);
        Assert.Contains("302", item.Summary);
        Assert.Equal(at, item.ObservedSinceUtc);
        Assert.Equal(at, item.LastObservedUtc);

        // The source itself stays healthy — that is the whole reason this block exists. A reader
        // looking only at Sources would see nothing wrong.
        Assert.DoesNotContain(response.Sources, s => s.SourceName == "nzbhydra2" && s.State == "open");
    }

    [Fact]
    public async Task A_successful_grab_retires_the_health_item()
    {
        using var client = _factory.CreateClient();
        var tracker = _factory.Services.GetRequiredService<IDownloadRefusalTracker>();
        await tracker.RecordRefusalAsync("nzbhydra2", "refused", DateTimeOffset.UtcNow);

        var beforeGrab = await client.GetFromJsonAsync<StatusResponse>("/api/status");
        Assert.Single(beforeGrab!.Health);

        await tracker.RecordSuccessfulGrabAsync("nzbhydra2");

        var afterGrab = await client.GetFromJsonAsync<StatusResponse>("/api/status");
        Assert.Empty(afterGrab!.Health);
    }

    [Fact]
    public async Task The_tracker_is_registered_as_a_singleton_so_writer_and_reader_share_one_instance()
    {
        // The feature is silently dead if the proxy writes to one instance and the endpoint reads
        // another, and every assertion above would still pass when both resolve the same scope.
        using var scopeA = _factory.Services.CreateScope();
        using var scopeB = _factory.Services.CreateScope();

        var a = scopeA.ServiceProvider.GetRequiredService<IDownloadRefusalTracker>();
        var b = scopeB.ServiceProvider.GetRequiredService<IDownloadRefusalTracker>();

        Assert.Same(a, b);
        await Task.CompletedTask;
    }
}
