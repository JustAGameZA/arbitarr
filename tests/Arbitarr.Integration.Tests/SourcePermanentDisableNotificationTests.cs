using System.Net;
using System.Text.Json;
using Arbitarr.Core.Notifications;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Notifications;
using Arbitarr.Data.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Arbitarr.Host.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-rx1f end to end against the REAL host composition: a search driven through the real
/// <c>ISourceRegistry</c> against a fake upstream answering 401, reaching the real
/// <c>BudgetedSourceRegistry</c>, the real <c>BudgetedUpstreamSource</c>, the real
/// <c>SourceBackoffStore</c>, the real <c>SourcePermanentDisableNotifier</c> and the real
/// <see cref="WebhookNotificationTransport"/>. Only the transport's HTTP handler and the upstream's
/// HTTP handler are replaced, so what is asserted is the body the operator's webhook would actually
/// have received.
///
/// <para><b>Why it resolves the REGISTRY rather than substituting one.</b> CLAUDE.md §4: #57's
/// webhook test passed with a real leak because it drove an endpoint that bypassed the dispatcher.
/// Replacing <c>ISourceRegistry</c> with a static one — as
/// <see cref="DownloadRefusalNotificationTests"/> legitimately does, because its feature lives on
/// the download path — would remove the very decorator this feature is wired into and prove nothing
/// about whether <c>Program.cs</c> composes a NOTIFYING gate. Dropping the notifier argument from
/// <c>Program.cs</c>'s <c>BudgetedSourceRegistry</c> factory fails this file and nothing else.</para>
///
/// <para>The upstream address is RFC 5737 TEST-NET-1 and the webhook URL an obviously-fake
/// <c>example.com</c> form: no real endpoint enters committed content.</para>
/// </summary>
public sealed class SourcePermanentDisableNotificationTests : IAsyncLifetime
{
    private const string SourceName = "permanent-disable-notification-fake-source";

    private const string WebhookUrl = "https://example.com/hooks/placeholder-permanent-disable-notify-token";

    // RFC 5737 TEST-NET-1: non-routable, and the handler below answers before anything is dialled.
    private const string UpstreamBaseUrl = "http://192.0.2.93:9117";

    /// <summary>
    /// The body a real indexer returns with a 401. It carries the rejected key, which is exactly why
    /// nothing derived from it may reach a webhook — that POST leaves this machine.
    /// </summary>
    private const string UpstreamRejectionBody =
        "<error code=\"100\" description=\"Incorrect user credentials: apikey placeholder-upstream-rejected-key\"/>";

    /// <summary>The key fragment alone, so a leak of only that still fails.</summary>
    private const string UpstreamKeyFragment = "placeholder-upstream-rejected-key";

    private readonly ArbitarrWebApplicationFactory _factory;
    private readonly CapturingHandler _webhook = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly string _configDirectory;

    public SourcePermanentDisableNotificationTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-permanent-disable-notification-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);

        _factory = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
    }

    /// <summary>
    /// The host with two handlers replaced and NOTHING else: the Newznab adapter's client, so the
    /// upstream rejects, and the webhook transport's client, so the notice is captured. The registry,
    /// its budget decorator, the backoff store and the notifier are all the real registered ones.
    /// </summary>
    private WebApplicationFactoryHost Host() => new(_factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient(Arbitarr.Host.Sources.SourceRegistry.NewznabHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RejectingUpstreamHandler());

            // The ONLY notification-path substitution: the transport's HTTP handler. The transport
            // type, the notifier, the gate decorator and the settings gate are all real.
            services.RemoveAll<WebhookNotificationTransport>();
            services.AddSingleton(new WebhookNotificationTransport(new HttpClient(_webhook)));

            // ADDITIVE, and not a substitution: an extra sink alongside whatever the host already
            // logs to. The delivery runs on a background task whose only account of itself is what
            // it logs, so without this a failure here can say "no notice arrived" and never why —
            // the undiagnosable shape CLAUDE.md §4 and arb-krtr both warn about.
            services.AddSingleton<ILoggerProvider>(_logs);
        })));

    /// <summary>A tiny holder so the built factory is disposed with the test that made it.</summary>
    private sealed class WebApplicationFactoryHost : IDisposable
    {
        public WebApplicationFactoryHost(
            Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
            => Factory = factory;

        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; }

        public void Dispose() => Factory.Dispose();
    }

    public async Task InitializeAsync() =>
        await _factory.SeedAsync(async db =>
        {
            var source = new Source
            {
                Kind = SourceRepository.NewznabKind,
                DisplayName = SourceName,
                BaseUrl = UpstreamBaseUrl,
                ApiPath = "/api",
                Priority = 10,
                TimeoutSeconds = 30,
                Enabled = true,
                LimitsUnit = "Day",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            db.Sources.Add(source);
            await db.SaveChangesAsync();

            db.Settings.Add(new SettingEntry
            {
                Name = SourceRepository.ApiKeySettingName(source.Id),
                Value = "secret-api-key-permanent-disable-probe",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        });

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        ConfigDirectoryTeardown.Delete(_configDirectory);
    }

    /// <summary>Answers every upstream call with the 401 an indexer gives a rejected key.</summary>
    private sealed class RejectingUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(UpstreamRejectionBody),
            });
    }

    /// <summary>
    /// Records the real serialized bodies the transport posted, and SIGNALS each arrival.
    ///
    /// <para><b>The signal is the point.</b> The notice is delivered from a fire-and-forget
    /// <c>Task.Run</c>, so a test has nothing to await unless the receiving end provides it. Polling
    /// a body count against a wall-clock deadline instead — which this file used to do — is not a
    /// wait on the condition at all: it is a fixed sleep wearing a loop, and under a loaded runner it
    /// expires while the delivery is still queued. Completing a
    /// <see cref="TaskCompletionSource{TResult}"/> the moment a body is read gives the test the real
    /// event to wait on, so the bound that remains is only a guard against hanging.</para>
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<string> _bodies = [];

        private readonly object _gate = new();

        /// <summary>
        /// Completed by the Nth POST. <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
        /// so a waiting test never resumes ON the transport's thread, which would let assertions run
        /// inside the delivery and make the handler's own bookkeeping race them.
        /// </summary>
        private readonly List<TaskCompletionSource<string>> _arrivals =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];

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

        /// <summary>
        /// A task completing when the <paramref name="ordinal"/>th notice (1-based) has been posted,
        /// created up front if that notice has not arrived yet. Asking BEFORE the event is what makes
        /// this a signal rather than a poll.
        /// </summary>
        public Task<string> Arrival(int ordinal)
        {
            lock (_gate)
            {
                while (_arrivals.Count < ordinal)
                {
                    _arrivals.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _arrivals[ordinal - 1].Task;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            TaskCompletionSource<string> arrived;
            lock (_gate)
            {
                _bodies.Add(body);

                while (_arrivals.Count < _bodies.Count)
                {
                    _arrivals.Add(new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                arrived = _arrivals[_bodies.Count - 1];
            }

            // Outside the lock: a continuation must never run while this handler holds the gate its
            // own next call needs.
            arrived.TrySetResult(body);

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    /// <summary>
    /// Collects what the notifier logged, so a failure to deliver can say WHY rather than only that
    /// nothing arrived. Thread-safe: written from the background delivery, read from the test.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly object _gate = new();

        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(string line)
        {
            lock (_gate)
            {
                _lines.Add(line);
            }
        }

        private sealed class Sink : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            private readonly string _category;

            public Sink(CapturingLoggerProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            /// <summary>
            /// Only the notifier's own category, and only Warning and worse. The host logs a great
            /// deal at Information that would bury the two lines this file needs, and a failure
            /// message is only useful if it is readable.
            /// </summary>
            public bool IsEnabled(LogLevel logLevel) =>
                logLevel >= LogLevel.Warning
                && _category.Contains(nameof(SourcePermanentDisableNotifier), StringComparison.Ordinal);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                _owner.Add($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }

    [Fact]
    public async Task A_rejected_key_on_the_real_search_path_posts_one_notice_carrying_no_upstream_text()
    {
        using var host = Host();

        using (var scope = host.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<NotificationRepository>().SetSettingsAsync(
                NotificationSettings.Default with { Enabled = true },
                WebhookUrl,
                CancellationToken.None);
        }

        // Drive a search through the REAL registry, which is what Program.cs decorates. The adapter
        // throws on the 401; the gate classifies it by STATUS CODE, records the outcome, and the
        // notifier computes the edge off the row.
        using (var scope = host.Factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider
                .GetRequiredService<Arbitarr.Core.Sources.ISourceRegistry>();

            var sources = await registry.ResolveAsync(CancellationToken.None);
            var source = Assert.Single(sources);

            await Assert.ThrowsAnyAsync<Exception>(() => source.SearchAsync(
                new Arbitarr.Core.Sources.SearchQuery(
                    "permanent.disable.probe",
                    Array.Empty<int>(),
                    Limit: 100,
                    Arbitarr.Core.Sources.SearchProtocol.Newznab),
                CancellationToken.None));
        }

        // The seam first, so the wait below is on a delivery already known to have finished rather
        // than on a wall clock racing a saturated thread pool.
        await WaitForDeliveriesAsync(host);

        var body = await WaitForOneNoticeAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("sourcePermanentlyDisabled", root.GetProperty("trigger").GetString());
        Assert.Equal(SourceName, root.GetProperty("sourceName").GetString());

        var message = root.GetProperty("message").GetString();
        Assert.NotNull(message);

        // It names the CONFIGURED source: an operator with three indexers has to be told which one
        // to go and fix, and told what to fix about it.
        Assert.Contains(SourceName, message!, StringComparison.Ordinal);
        Assert.Contains("is disabled. Searches skip it.", message!, StringComparison.Ordinal);

        // The state really is durably disabled, so the notice describes something that happened
        // rather than passing for the wrong reason.
        using (var scope = host.Factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<SourceBackoffStore>()
                .GetAsync(SourceName, CancellationToken.None);

            Assert.NotNull(state);
            Assert.True(state!.IsPermanentlyDisabled);
        }

        // DETECTABILITY CONTROL, and it must come BEFORE the absence assertions. This is the message
        // the regression would have produced — the upstream rejection text interpolated into the
        // summary, which is the obvious "make the notice more informative" change. Both searches are
        // run against it FIRST and are required to HIT, proving each can find the value at all.
        var leaked = SummarizeAsALeakWould();
        Assert.Contains(UpstreamRejectionBody, leaked, StringComparison.Ordinal);
        Assert.Contains(UpstreamKeyFragment, leaked, StringComparison.Ordinal);

        // Only now do the misses mean anything, and they are run over the WHOLE body rather than
        // the message, so a leak into any other field fails too.
        Assert.DoesNotContain(UpstreamRejectionBody, body, StringComparison.Ordinal);
        Assert.DoesNotContain(UpstreamKeyFragment, body, StringComparison.Ordinal);

        // Nor Arbitarr's own stored key, the upstream URL, or the webhook target.
        Assert.DoesNotContain("secret-api-key-permanent-disable-probe", body, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.93", body, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookUrl, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_search_against_the_disabled_source_posts_nothing_further()
    {
        using var host = Host();

        using (var scope = host.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<NotificationRepository>().SetSettingsAsync(
                NotificationSettings.Default with { Enabled = true },
                WebhookUrl,
                CancellationToken.None);
        }

        // POSITIVE CONTROL: the first search DOES notify, proving the capture is wired and would
        // have seen a second notice had one been sent.
        await SearchOnceAsync(host);

        // The EDGE is what produces a notice, and it is computed from this row. Asserting it here
        // separates "the source was never disabled, so there was correctly nothing to announce" from
        // "it was disabled and the announcement went missing" — two failures that otherwise both
        // surface as an empty capture, which is the undiagnosable shape this file keeps fighting.
        using (var scope = host.Factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<SourceBackoffStore>()
                .GetAsync(SourceName, CancellationToken.None);

            Assert.True(
                state?.IsPermanentlyDisabled == true,
                "The first search did not permanently disable the source, so no edge was raised and "
                + "no notice was due. The failure is upstream of the notifier: the search did not "
                + "record a rejecting outcome. Observed state: "
                + (state is null ? "no backoff row at all." : $"IsPermanentlyDisabled={state.IsPermanentlyDisabled}."));
        }

        await WaitForDeliveriesAsync(host);
        await WaitForOneNoticeAsync();

        // Three more searches. The gate now refuses the source outright, so these are skips — and a
        // skip must not re-announce a condition the operator has already been told about.
        for (var i = 0; i < 3; i++)
        {
            await SearchOnceAsync(host);
        }

        // NO TIMED WAIT HERE, and that is the point of the seam. This used to be a bounded
        // Task.WhenAny against a fixed delay, defended as unavoidable on the grounds that an event
        // which never happens offers nothing to await. That reasoning was wrong once the notifier
        // began counting its deliveries: a notice raised by any of those three searches would have
        // been COUNTED before its task was scheduled, so DeliveriesIdle cannot complete while one is
        // pending. Waiting for it and finding nothing posted is therefore a real observation rather
        // than a guess that enough time has passed.
        //
        // It is also not vacuous (CLAUDE.md §4). DeliveriesIdle completes immediately when nothing
        // was ever raised, so on its own it would pass against a notifier that never notifies. The
        // POSITIVE CONTROL above is what makes it bite: the same seam, awaited the same way, was
        // required to yield a real posted notice a few lines earlier.
        await WaitForDeliveriesAsync(host);

        Assert.Single(_webhook.Bodies);
    }

    /// <summary>
    /// Waits for every notice raised so far to finish being delivered.
    ///
    /// <para><b>This is what makes the assertions deterministic.</b> A search returns once the
    /// outcome is RECORDED; the notice is raised afterwards on a fire-and-forget task that nothing
    /// awaits. So a search completing says nothing about whether its notice has been posted, and a
    /// test reading the capture straight afterwards is racing the delivery. That race is not
    /// theoretical: it failed a full local run of this suite, having waited the entire budget and
    /// found the notifier had not logged a single line, because the queued work had not yet reached
    /// the head of a saturated pool.</para>
    ///
    /// <para><b>The timeout is a HANG GUARD ONLY, not the synchronisation.</b> The real wait is on
    /// <see cref="SourcePermanentDisableNotifier.DeliveriesIdle"/>, which completes when the work
    /// actually finishes, however long the pool makes that take. The bound exists so a delivery that
    /// never completes at all fails by name instead of hanging until the test host gives up.</para>
    /// </summary>
    private static async Task WaitForDeliveriesAsync(WebApplicationFactoryHost host)
    {
        var notifier = host.Factory.Services.GetRequiredService<SourcePermanentDisableNotifier>();

        try
        {
            await notifier.DeliveriesIdle.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                "A source permanent-disable delivery never completed. The notifier still reports "
                + "work in flight, so it is stuck rather than finished-and-silent.");
        }
    }

    /// <summary>
    /// Resolves the real gated source and searches once, swallowing whatever it throws. A
    /// permanently disabled source is SKIPPED rather than failed, so the later calls return an empty
    /// list instead of throwing — both shapes are fine here, because what is being counted is
    /// notices.
    /// </summary>
    private static async Task SearchOnceAsync(WebApplicationFactoryHost host)
    {
        using var scope = host.Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<Arbitarr.Core.Sources.ISourceRegistry>();
        var sources = await registry.ResolveAsync(CancellationToken.None);

        foreach (var source in sources)
        {
            try
            {
                await source.SearchAsync(
                    new Arbitarr.Core.Sources.SearchQuery(
                        "permanent.disable.probe",
                        Array.Empty<int>(),
                        Limit: 100,
                        Arbitarr.Core.Sources.SearchProtocol.Newznab),
                    CancellationToken.None);
            }
            catch
            {
                // The 401 surfaces as the adapter's exception. The outcome has already been recorded
                // by the gate at this point, which is the only thing this helper is arranging.
            }
        }
    }

    /// <summary>
    /// The message a leaking implementation WOULD have built, constructed here so the detectability
    /// control has something to go red against WITHOUT putting vulnerable code in the product
    /// (CLAUDE.md §4). Nothing but that control calls it.
    /// </summary>
    private static string SummarizeAsALeakWould() =>
        $"Source '{SourceName}' rejected Arbitarr's API key and is disabled: {UpstreamRejectionBody}";

    /// <summary>
    /// Waits for the fire-and-forget delivery to land, on the RECEIVER'S OWN SIGNAL.
    ///
    /// <para>The notifier hands off to <c>Task.Run</c> by design — that is what keeps a webhook POST
    /// off the search path — so there is nothing on the production side to await. The previous
    /// version of this method polled a body count until a 15 second deadline and called that waiting
    /// on the condition; it was not. A deadline is a fixed sleep however it is spelled, and it
    /// expired on a loaded CI runner while the delivery was still pending, failing the test with no
    /// indication of why. What is awaited now is the <see cref="CapturingHandler.Arrival"/> signal
    /// the fake receiver raises when it has actually read a body.</para>
    ///
    /// <para><b>The bound that remains is a HANG GUARD, not the synchronisation.</b> Callers reach
    /// here having already awaited <see cref="WaitForDeliveriesAsync"/>, so the delivery is finished
    /// and the body is expected to be present already; this bound only stops an unwired gate from
    /// hanging until the test host gives up. The message carries whatever the notifier logged, so a
    /// failure says WHICH stage was reached. Only the notifier's own Warning-and-worse lines are
    /// captured, and its two log statements both name a closed-enum trigger and outcome and never
    /// the target, so no webhook URL, source address or key can reach this message — which matters
    /// because this repository and its CI logs are public (CLAUDE.md §1).</para>
    /// </summary>
    private async Task<string> WaitForOneNoticeAsync()
    {
        try
        {
            return await _webhook.Arrival(1).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            var logged = _logs.Lines;
            Assert.Fail(
                "No notification was posted even though every delivery has completed; the composed "
                + "host may not wire the notifying gate, or the delivery failed before the POST. "
                + (logged.Count == 0
                    ? "The notifier logged nothing, so it never ran at all."
                    : $"The notifier logged: {string.Join(" | ", logged)}"));
            return string.Empty;
        }
    }
}
