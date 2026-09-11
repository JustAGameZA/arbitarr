using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Backup;
using Arbitarr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-v3w: a refused download recorded by one host is visible on <c>/api/status</c> from a FRESH
/// host built over the same database file.
///
/// <para>This is the whole bead, end to end. The NZBHydra2 "NZB access type: Redirect"
/// misconfiguration behind a refusal outlives the process, so the old in-memory-only tracker made a
/// restart report a clean dashboard while every download still failed — the exact invisibility
/// ADR 0014 records as the original defect.</para>
///
/// <para><b>Each test owns its factories</b> rather than sharing the assembly's class fixture,
/// because "a fresh host over a different database shows none" is only honest if nothing else has
/// written to either database first.</para>
/// </summary>
public sealed class StatusHealthItemsSurviveRestartTests : IDisposable
{
    private static readonly DateTimeOffset First = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    private readonly string _sharedConfigDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-v3w-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// THIS CLASS owns the shared directory, because a factory built by
    /// <see cref="ArbitarrWebApplicationFactory.OverConfigDirectory"/> deliberately does not delete
    /// it — a first host that took the database with it would leave the second host rehydrating from
    /// nothing, and the restart assertion would pass for the wrong reason.
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(_sharedConfigDirectory))
        {
            return;
        }

        // The same two clears the factory does, for the same reason: a pooled handle still holds a
        // share lock on Windows, so the delete loses to it without them. Never ClearAllPools —
        // it is process-global and banned from test IL (docs/standards/data.md).
        SqlitePoolCleaner.ClearPoolsFor(new BackupPaths(_sharedConfigDirectory).DatabasePath);
        SqlitePools.ClearPoolsForDirectory(_sharedConfigDirectory);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                Directory.Delete(_sharedConfigDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                {
                    // Swallowed rather than thrown: faulting from Dispose would surface on whichever
                    // unrelated test is in flight rather than on this one.
                    return;
                }

                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Reads <c>/api/status</c>, retrying briefly while the health block is empty.
    ///
    /// <para><b>Why a poll and not a single read.</b> Rehydration runs in a
    /// <c>BackgroundService.ExecuteAsync</c>, which is NOT awaited before the host reports started —
    /// the same property <c>StagingSweepIntegrationTests</c> polls for. So a request can legitimately
    /// arrive before the load has finished. The poll is bounded and the FAILURE mode is the
    /// interesting one: if rehydration never happens, this returns an empty block and the caller's
    /// assertion fails, which is the regression being guarded.</para>
    /// </summary>
    private static async Task<IReadOnlyList<HealthItem>> ReadHealthAsync(HttpClient client, bool expectItems)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var response = await client.GetFromJsonAsync<StatusResponse>("/api/status");
            Assert.NotNull(response);

            if (!expectItems || response!.Health.Count > 0)
            {
                return response!.Health;
            }

            await Task.Delay(100);
        }

        // One last read so the caller asserts against a real response rather than a timeout.
        var final = await client.GetFromJsonAsync<StatusResponse>("/api/status");
        return final!.Health;
    }

    [Fact]
    public async Task A_refusal_recorded_by_one_host_is_visible_from_a_fresh_host_over_the_same_database()
    {
        await using (var first = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory))
        {
            using var client = first.CreateClient();
            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();

            await tracker.RecordRefusalAsync(
                "nzbhydra2",
                "Refused HTTP 302: the source redirected instead of serving the file.",
                First);

            // The first host shows it — the pre-existing arb-ln0 behaviour, asserted here so a
            // failure in the second host below is attributable to REHYDRATION rather than to the
            // refusal never having been recorded at all.
            var item = Assert.Single(await ReadHealthAsync(client, expectItems: true));
            Assert.Equal("nzbhydra2", item.SourceName);
        }

        // A genuinely separate host: its own DI container, its own singleton tracker, its own
        // startup sequence. Only the database file is shared.
        await using var second = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory);
        using var secondClient = second.CreateClient();

        var rehydrated = Assert.Single(await ReadHealthAsync(secondClient, expectItems: true));
        Assert.Equal("download-refused-redirect", rehydrated.Key);
        Assert.Equal("blocking", rehydrated.Severity);
        Assert.Equal("nzbhydra2", rehydrated.SourceName);
        Assert.Contains("302", rehydrated.Summary);

        // THE POINT OF PERSISTING IT: the instant is the one the FIRST host recorded, not this
        // process's start. A rehydration that reset this to "now" would restore the item while
        // still lying about how long the condition has been running.
        Assert.Equal(First, rehydrated.ObservedSinceUtc);
    }

    /// <summary>
    /// THE POSITIVE CONTROL for the test above. A fresh host over a DIFFERENT database shows no
    /// health item — so the rehydration asserted there is genuinely reading the shared file, rather
    /// than any new host happening to report an item for some unrelated reason.
    /// </summary>
    [Fact]
    public async Task A_fresh_host_over_a_different_database_shows_no_health_item()
    {
        await using (var first = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory))
        {
            using var client = first.CreateClient();
            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();

            await tracker.RecordRefusalAsync("nzbhydra2", "Refused HTTP 302: the source redirected instead of serving the file.", First);

            // Proves the refusal WAS recorded and IS detectable — without which the emptiness
            // asserted below would pass just as happily on a refusal that never happened.
            Assert.Single(await ReadHealthAsync(client, expectItems: true));
        }

        // A default factory: its own fresh, GUID-distinct config directory.
        await using var unrelated = new ArbitarrWebApplicationFactory();
        using var unrelatedClient = unrelated.CreateClient();

        Assert.Empty(await ReadHealthAsync(unrelatedClient, expectItems: false));
    }

    [Fact]
    public async Task A_successful_grab_in_one_host_stops_the_next_host_from_showing_the_item()
    {
        // Clear-only-on-success has to survive the restart too: an item retired by a real grab must
        // stay retired, or the banner would reappear at every restart after the operator fixed it.
        await using (var first = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory))
        {
            using var client = first.CreateClient();
            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();

            await tracker.RecordRefusalAsync("nzbhydra2", "refused", First);
            Assert.Single(await ReadHealthAsync(client, expectItems: true));

            await tracker.RecordSuccessfulGrabAsync("nzbhydra2");
            Assert.Empty(await ReadHealthAsync(client, expectItems: false));
        }

        await using var second = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory);
        using var secondClient = second.CreateClient();

        Assert.Empty(await ReadHealthAsync(secondClient, expectItems: false));
    }
}
