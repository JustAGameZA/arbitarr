using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Dashboard;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Notifications;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Notifications;
using Arbitarr.Host.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>arb-pu58: a source that HAS a row in the sources table, so its refusal must survive.</summary>
    private const string ConfiguredSource = "nzbhydra2";

    /// <summary>
    /// arb-pu58: a source that has NO row — the state left behind by deleting a source while its
    /// refusal stands. Named so a failure message says which side of the prune went wrong.
    /// </summary>
    private const string RemovedSource = "removed-source";

    private readonly string _sharedConfigDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-v3w-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// THIS CLASS owns the shared directory, because a factory built by
    /// <see cref="ArbitarrWebApplicationFactory.OverConfigDirectory"/> deliberately does not delete
    /// it — a first host that took the database with it would leave the second host rehydrating from
    /// nothing, and the restart assertion would pass for the wrong reason.
    ///
    /// <para>Routed through <see cref="ConfigDirectoryTeardown"/> (arb-gphi) rather than carrying its
    /// own copy of the clear-then-delete block, which is what this class was written with. The helper
    /// does the same two clears for the same reason — a pooled handle holds a share lock on Windows,
    /// so the delete loses to it without them, and never <c>ClearAllPools</c>, which is process-global
    /// and banned from test IL (docs/standards/data.md).</para>
    ///
    /// <para><b>Delete rather than TryDelete, so a failure is not swallowed.</b> This class OWNS the
    /// directory, so a teardown that stops working is this class's defect and should fail it — the
    /// point of arb-gphi being that a swallowed cleanup failure is indistinguishable from a working
    /// one and stayed invisible for ~22,000 directories. The factories use <c>TryDelete</c> instead
    /// because xunit disposes them as class fixtures, where a throw lands on whichever unrelated test
    /// is in flight; a test class disposing its own directory has no such problem.</para>
    /// </summary>
    public void Dispose() => ConfigDirectoryTeardown.Delete(_sharedConfigDirectory);

    /// <summary>
    /// Reads <c>/api/status</c> ONCE.
    ///
    /// <para><b>A single read, deliberately, and it must stay one.</b>
    /// <c>DownloadRefusalRehydrationService</c> is a plain <c>IHostedService</c> whose
    /// <c>StartAsync</c> is awaited before the host serves anything, so by the time this request is
    /// answered the load has necessarily finished. This used to poll 50×100ms, back when rehydration
    /// ran in a not-awaited <c>BackgroundService</c>. Restoring any retry here would re-hide exactly
    /// the window that change closed: a regression that pushed rehydration back off the startup path
    /// would be papered over by the wait instead of failing.</para>
    /// </summary>
    private static async Task<IReadOnlyList<HealthItem>> ReadHealthAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<StatusResponse>("/api/status");
        Assert.NotNull(response);
        return response!.Health;
    }

    /// <summary>
    /// Adds a real <c>Sources</c> row for <paramref name="displayName"/>.
    ///
    /// <para><b>arb-pu58 made this setup load-bearing rather than incidental.</b> Rehydration now
    /// prunes refusal rows whose source has no row in that table, so a test that records a refusal
    /// against a name the sources table has never heard of is describing the GHOST case, not the
    /// survives-a-restart case. These tests are about durability for a source that still exists, so
    /// the source has to actually exist — which is also what the production path looks like, since a
    /// refusal can only be recorded for a source that served a download attempt.</para>
    /// </summary>
    private static async Task AddSourceAsync(ArbitarrWebApplicationFactory factory, string displayName)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();

        dbContext.Sources.Add(new Source
        {
            Kind = SourceSeeder.NzbHydraKind,
            DisplayName = displayName,
            BaseUrl = "https://nzbhydra.example.invalid",
            Enabled = true,
            CreatedAt = First,
            UpdatedAt = First,
        });

        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task A_refusal_recorded_by_one_host_is_visible_from_a_fresh_host_over_the_same_database()
    {
        await using (var first = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory))
        {
            using var client = first.CreateClient();
            await AddSourceAsync(first, "nzbhydra2");
            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();

            await tracker.RecordRefusalAsync(
                "nzbhydra2",
                "Refused HTTP 302: the source redirected instead of serving the file.",
                First);

            // The first host shows it — the pre-existing arb-ln0 behaviour, asserted here so a
            // failure in the second host below is attributable to REHYDRATION rather than to the
            // refusal never having been recorded at all.
            var item = Assert.Single(await ReadHealthAsync(client));
            Assert.Equal("nzbhydra2", item.SourceName);
        }

        // A genuinely separate host: its own DI container, its own singleton tracker, its own
        // startup sequence. Only the database file is shared.
        await using var second = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory);
        using var secondClient = second.CreateClient();

        var rehydrated = Assert.Single(await ReadHealthAsync(secondClient));
        Assert.Equal(StatusEndpoint.DownloadRefusedRedirectKey, rehydrated.Key);
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

            // arb-pu58: the source must EXIST, or the emptiness below would be the prune's doing
            // rather than the separate-database's, and this test would stop testing its own subject.
            await AddSourceAsync(first, "nzbhydra2");
            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();

            await tracker.RecordRefusalAsync("nzbhydra2", "Refused HTTP 302: the source redirected instead of serving the file.", First);

            // Proves the refusal WAS recorded and IS detectable — without which the emptiness
            // asserted below would pass just as happily on a refusal that never happened.
            Assert.Single(await ReadHealthAsync(client));
        }

        // A default factory: its own fresh, GUID-distinct config directory.
        await using var unrelated = new ArbitarrWebApplicationFactory();
        using var unrelatedClient = unrelated.CreateClient();

        Assert.Empty(await ReadHealthAsync(unrelatedClient));
    }

    [Fact]
    public async Task A_successful_grab_in_one_host_stops_the_next_host_from_showing_the_item()
    {
        // Clear-only-on-success has to survive the restart too: an item retired by a real grab must
        // stay retired, or the banner would reappear at every restart after the operator fixed it.
        await using (var first = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory))
        {
            using var client = first.CreateClient();

            // arb-pu58: the source must EXIST, or the final assertion would hold because the prune
            // removed the row rather than because the GRAB did — and this test would silently stop
            // covering clear-on-success, the thing it is named for.
            await AddSourceAsync(first, "nzbhydra2");
            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();

            await tracker.RecordRefusalAsync("nzbhydra2", "refused", First);
            Assert.Single(await ReadHealthAsync(client));

            await tracker.RecordSuccessfulGrabAsync("nzbhydra2");
            Assert.Empty(await ReadHealthAsync(client));
        }

        await using var second = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory);
        using var secondClient = second.CreateClient();

        Assert.Empty(await ReadHealthAsync(secondClient));
    }

    /// <summary>
    /// arb-v3w × arb-apj: restoring a persisted refusal at startup raises NO notification.
    ///
    /// <para>This is the interaction between the two features, and it is the one a naive composition
    /// gets wrong. Rehydration replays into the INNER concrete tracker, not through
    /// <c>NotifyingDownloadRefusalTracker</c>, so the decorator never sees a none-&gt;present edge for
    /// a condition that did not just begin. Had rehydration been pushed through the outer decorator
    /// instead, every restart would re-page the operator about a refusal they already know about —
    /// and a service that restarts on a crash loop would do it repeatedly.</para>
    ///
    /// <para><b>The positive control is the second half</b>, and it is what makes the silence mean
    /// anything. A bare "zero notices after restart" passes just as happily when the webhook was
    /// never wired, the settings gate was off, or the handler was never reached. So the SAME host,
    /// through the SAME capturing handler, is then made to record a genuinely new refusal — and that
    /// one MUST deliver. Only then is the preceding zero real silence rather than a dead path.</para>
    /// </summary>
    [Fact]
    public async Task A_rehydrated_refusal_raises_no_notification_while_a_fresh_one_still_does()
    {
        await using (var first = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory))
        {
            using var client = first.CreateClient();
            await AddSourceAsync(first, "nzbhydra2");
            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();

            await tracker.RecordRefusalAsync("nzbhydra2", "refused", First);
            Assert.Single(await ReadHealthAsync(client));
        }

        // The restarted host, with only its webhook HTTP handler replaced so the notifier, the
        // settings gate and the tracker composition are all the real registered ones.
        var handler = new CapturingHandler();
        await using var restarted = ArbitarrWebApplicationFactory
            .OverConfigDirectory(_sharedConfigDirectory)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<WebhookNotificationTransport>();
                services.AddSingleton(new WebhookNotificationTransport(new HttpClient(handler)));
            }));

        using var restartedClient = restarted.CreateClient();

        using (var scope = restarted.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<NotificationRepository>().SetSettingsAsync(
                NotificationSettings.Default with { Enabled = true },
                "https://hooks.example.invalid/arb-v3w-restart-probe",
                CancellationToken.None);
        }

        // The item IS back — so the zero below is about the notification, not about rehydration
        // having failed and left nothing to notify about.
        Assert.Single(await ReadHealthAsync(restartedClient));

        // THE CONTROL, driven FIRST rather than after a fixed sleep: a genuinely new edge, on a
        // source that was NOT rehydrated, through this same host and handler. Waiting on its
        // delivery (rather than a flat 750ms) is what makes the silence assertion below meaningful
        // instead of a race against an arbitrary duration — a slower CI box could not turn a real
        // rehydration-notifies regression into a false pass, and a faster one could not turn a
        // genuine bug into a false failure.
        var restartedTracker = restarted.Services.GetRequiredService<IDownloadRefusalTracker>();
        await restartedTracker.RecordRefusalAsync("other-source", "refused", First.AddHours(1));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (handler.Bodies.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var delivered = Assert.Single(handler.Bodies);
        Assert.Contains("other-source", delivered, StringComparison.Ordinal);

        // THE SILENCE ASSERTION: by the time the control's delivery has landed, any delivery that
        // rehydration itself would have raised has necessarily already landed too — both are posted
        // from the same fire-and-forget path on the same host. So the rehydrated source's continued
        // absence here is not "we didn't wait long enough", it is "it was never posted".
        Assert.DoesNotContain("nzbhydra2", delivered, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-pu58: a refusal row for a source that is NO LONGER CONFIGURED is discarded at rehydration
    /// instead of becoming a permanent blocking item for a source that does not exist.
    ///
    /// <para><b>Why this cannot resolve itself without the prune.</b> A row is deleted on exactly one
    /// event — a successful grab from that source. Remove the source and that event can never happen
    /// again, so the item is not merely stale, it is unclearable: every restart replays it, and since
    /// arb-apj a notification goes with it.</para>
    ///
    /// <para><b>Both halves are load-bearing and the test asserts both.</b> The unknown source's row
    /// must go, and a still-configured source's row must SURVIVE the same pass — otherwise a blanket
    /// "delete everything at startup" would pass the first assertion while destroying the durability
    /// arb-v3w exists to provide. Asserting only the disappearance would not tell those apart.</para>
    ///
    /// <para>The first host asserts BOTH items are present before the restart. That is the positive
    /// control: without it the post-restart absence of the orphan would pass just as happily had the
    /// refusal never been recorded, or had the whole health surface been empty for some unrelated
    /// reason.</para>
    /// </summary>
    [Fact]
    public async Task A_refusal_row_for_a_source_that_is_no_longer_configured_is_pruned_at_restart()
    {
        await using (var first = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory))
        {
            using var client = first.CreateClient();

            // A source that genuinely EXISTS in the sources table, so its refusal is the one the
            // prune must keep. RemovedSource is deliberately NOT added — that absence is the whole
            // condition under test.
            await AddSourceAsync(first, ConfiguredSource);

            var tracker = first.Services.GetRequiredService<IDownloadRefusalTracker>();
            await tracker.RecordRefusalAsync(ConfiguredSource, "refused", First);
            await tracker.RecordRefusalAsync(RemovedSource, "refused", First);

            // THE POSITIVE CONTROL: both rows exist and both are visible, so the selective absence
            // asserted after the restart is genuinely the prune having run.
            var before = await ReadHealthAsync(client);
            Assert.Equal(
                new[] { ConfiguredSource, RemovedSource },
                before.Select(i => i.SourceName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        // The restart. RemovedSource was never in the sources table — the state an operator leaves
        // behind by deleting a source while its refusal stands.
        await using var second = ArbitarrWebApplicationFactory.OverConfigDirectory(_sharedConfigDirectory);
        using var secondClient = second.CreateClient();

        var item = Assert.Single(await ReadHealthAsync(secondClient));
        Assert.Equal(ConfiguredSource, item.SourceName);

        // AND THE ROW IS GONE FROM THE TABLE, not merely filtered out of this response. Had the fix
        // filtered at read time instead, this would still hold a row and the item would come back on
        // any later start whose filter was less careful.
        using (var scope = second.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
            var remaining = await dbContext.DownloadRefusalEntries
                .AsNoTracking()
                .Select(e => e.SourceName)
                .ToListAsync();

            Assert.Equal([ConfiguredSource], remaining);
        }
    }

    /// <summary>Records the real serialized bodies the transport posted.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<string> _bodies = [];
        private readonly object _gate = new();

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_gate)
                {
                    return _bodies.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_gate)
            {
                _bodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
