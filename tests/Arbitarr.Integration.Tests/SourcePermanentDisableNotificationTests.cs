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

    /// <summary>
    /// The upstream stub, held as ONE instance for the whole test rather than newed per handler
    /// chain, so its request log is the complete account of what the search actually dialled.
    ///
    /// <para>This is not merely convenience. <c>ConfigurePrimaryHttpMessageHandler</c> takes a
    /// FACTORY, and <c>IHttpClientFactory</c> rotates a named client's handler chain on its own
    /// schedule — so a per-call <c>new</c> would spread the requests across instances the test
    /// cannot see, and an empty count would then be indistinguishable between "the search never
    /// dialled" and "it dialled a sibling instance". One shared instance makes the count mean
    /// exactly one thing. It is safe to share: every member is guarded by its own lock.</para>
    /// </summary>
    private readonly RejectingUpstreamHandler _upstream = new();

    private readonly object _searchGate = new();

    /// <summary>What each search the test drove did, in order — see <see cref="DiagnoseAsync"/>.</summary>
    private readonly List<string> _searchOutcomes = [];

    /// <summary>
    /// How many sources the registry resolved on the last search. Zero would mean the loop that
    /// drives the search never executed at all, which reads identically to a search that recorded
    /// nothing — and is a wholly different cause.
    /// </summary>
    private int _resolvedSourceCount = -1;

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
            // The SAME stub instance every time the chain is built — see the field's doc for why a
            // per-call `new` would make an empty request count ambiguous. Handed out behind a
            // non-disposing wrapper because IHttpClientFactory DISPOSES a primary handler when it
            // retires a chain, and a disposed shared stub would answer nothing for the rest of the
            // test while looking exactly like a search that never dialled.
            services.AddHttpClient(Arbitarr.Host.Sources.SourceRegistry.NewznabHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new NonDisposingHandler(_upstream));

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
        private readonly object _gate = new();

        private readonly List<string> _paths = [];

        /// <summary>
        /// The ABSOLUTE PATH of every request this stub answered, in order — never the query string,
        /// which is where the API key travels.
        ///
        /// <para>A failure needs this to tell two very different causes apart: an EMPTY list means
        /// the search never reached the stub, so a recorded non-auth outcome came from something
        /// upstream of the 401 rather than from a misclassified 401; a non-empty one means the 401
        /// was answered and the classification is where to look. The path alone also separates a
        /// caps call from a search.</para>
        ///
        /// <para>The query is DROPPED rather than scrubbed. This repository and its CI logs are
        /// public (CLAUDE.md §1), and a dropped field cannot later be defeated by a scrubbing
        /// pattern that fails to match a form nobody anticipated.</para>
        /// </summary>
        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_gate)
                {
                    return _paths.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Answer(request);

        /// <summary>
        /// The 401, with the request's path recorded first. Public because
        /// <see cref="NonDisposingHandler"/> forwards to it and <c>SendAsync</c> is protected.
        /// </summary>
        public Task<HttpResponseMessage> Answer(HttpRequestMessage request)
        {
            lock (_gate)
            {
                _paths.Add(request.RequestUri?.AbsolutePath ?? "(no request URI)");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(UpstreamRejectionBody),
            });
        }
    }

    /// <summary>
    /// Forwards to an inner handler and does NOT dispose it, so one stub instance can back every
    /// handler chain <c>IHttpClientFactory</c> builds for the named client. Nothing else: it adds no
    /// behaviour to the response path.
    /// </summary>
    private sealed class NonDisposingHandler : HttpMessageHandler
    {
        private readonly RejectingUpstreamHandler _inner;

        public NonDisposingHandler(RejectingUpstreamHandler inner) => _inner = inner;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _inner.Answer(request);

        /// <summary>Deliberately does not dispose <c>_inner</c> — that is this type's whole purpose.</summary>
        protected override void Dispose(bool disposing)
        {
        }
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

        var body = await WaitForOneNoticeAsync(host);

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

            // The condition is unchanged; only what a failure REPORTS has grown. The previous
            // message said no more than IsPermanentlyDisabled=False, which proved a row existed but
            // left Success and TransientFailure — two causes needing opposite fixes — indistinct,
            // and cost a full CI round to get no further. DiagnoseAsync names the recorded outcome
            // and whether the stub answered at all.
            // Built only on the failing branch rather than passed to Assert.True, whose message
            // argument is evaluated EAGERLY — that would run the whole diagnostic, extra scopes and
            // database reads included, on every passing run.
            if (state?.IsPermanentlyDisabled != true)
            {
                Assert.Fail(
                    "The first search did not permanently disable the source, so no edge was raised "
                    + "and no notice was due. The failure is upstream of the notifier: the search "
                    + "did not record a rejecting outcome. Observed state: "
                    + (state is null
                        ? "no backoff row at all."
                        : $"IsPermanentlyDisabled={state.IsPermanentlyDisabled}.")
                    + " Diagnostics: " + await DiagnoseAsync(host));
            }
        }

        await WaitForDeliveriesAsync(host);
        await WaitForOneNoticeAsync(host);

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
    /// Everything a red run of this file needs to name its own cause, in one line.
    ///
    /// <para><b>Why it exists.</b> Both failure messages in this file previously narrowed the cause
    /// without naming it. The CI failure that prompted this said only
    /// <c>IsPermanentlyDisabled=False</c> — enough to prove a backoff row had been written and that
    /// the outcome was therefore neither <c>AuthenticationFailure</c> (which sets the flag) nor
    /// <c>NotAttempted</c> (which writes no row at all), but not enough to say WHICH of the two
    /// remaining outcomes it was. <c>Success</c> and <c>TransientFailure</c> need opposite fixes:
    /// the first means a call that never happened was recorded as healthy, the second means the 401
    /// stub was not what answered. <c>LastOutcome</c> is written on every path, so the row already
    /// knew; the message simply did not ask. One expensive CI round per un-asked field is the cost
    /// this removes.</para>
    ///
    /// <para><b>Nothing here can carry a secret</b>, and that is by construction rather than by
    /// scrubbing — this repository and its CI logs are public (CLAUDE.md §1). The stub reports
    /// PATHS only, never query strings, so the API key cannot ride along. The planted key is
    /// reported as a BOOLEAN (present or not), never echoed. The captured log lines are already
    /// restricted to the notifier's own category at Warning and worse, whose two statements name a
    /// closed-enum trigger and outcome and never a URL, address or key — this reads that existing
    /// capture and deliberately does not widen it, since a wider sink would pull in categories
    /// whose lines carry values nobody here has vetted.</para>
    /// </summary>
    private async Task<string> DiagnoseAsync(WebApplicationFactoryHost host)
    {
        var parts = new List<string>();

        using (var scope = host.Factory.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<SourceBackoffStore>()
                .GetAsync(SourceName, CancellationToken.None);

            parts.Add(state is null
                ? "backoff row: NONE (no outcome was ever recorded for this source)"
                : $"backoff row: LastOutcome={state.LastOutcome ?? "(null)"}, "
                  + $"DisabledLevel={state.DisabledLevel}, "
                  + $"IsPermanentlyDisabled={state.IsPermanentlyDisabled}, "
                  + $"DisabledUntil={state.DisabledUntil?.ToString("O") ?? "(null)"}, "
                  + $"UpdatedAt={state.UpdatedAt:O}");
        }

        // The stub's own account of what it was asked. An empty list is the single most valuable
        // fact this message can carry: it moves the search from "misclassified the 401" to "never
        // reached the 401 at all", which are different bugs in different files.
        var paths = _upstream.Paths;
        parts.Add(paths.Count == 0
            ? "upstream stub: answered 0 requests (the search never reached it)"
            : $"upstream stub: answered {paths.Count} request(s), paths (query omitted): "
              + string.Join(", ", paths));

        lock (_searchGate)
        {
            parts.Add($"sources resolved by the registry: {_resolvedSourceCount}");
            parts.Add(_searchOutcomes.Count == 0
                ? "searches driven: none"
                : $"searches driven ({_searchOutcomes.Count}): " + string.Join(" then ", _searchOutcomes));
        }

        // The breaker is the one gate that can make a search return EMPTY without throwing, so a
        // recorded Success with an Open breaker and a zero stub count is a complete diagnosis on its
        // own. Resolved defensively: this is a diagnostic, and it must never be the reason a test
        // fails with something other than the assertion that called it.
        try
        {
            using var scope = host.Factory.Services.CreateScope();
            var breaker = scope.ServiceProvider
                .GetRequiredService<Arbitarr.Core.Sources.CircuitBreaker.SourceCircuitBreaker>();
            var snapshot = breaker.GetSnapshot(SourceName);

            parts.Add(
                $"breaker: State={snapshot.State}, ConsecutiveFailures={snapshot.ConsecutiveFailures}, "
                + $"CurrentBackoff={snapshot.CurrentBackoff}, "
                + $"NextProbeAt={snapshot.NextProbeAt?.ToString("O") ?? "(null)"}, "
                + $"LastFailureAt={snapshot.LastFailureAt?.ToString("O") ?? "(null)"}, "
                + $"LastSuccessAt={snapshot.LastSuccessAt?.ToString("O") ?? "(null)"}");
        }
        catch (Exception ex)
        {
            parts.Add($"breaker: not reachable from the composed host ({ex.GetType().Name})");
        }

        var logged = _logs.Lines;
        parts.Add(logged.Count == 0
            ? "notifier log (Warning+): nothing, so the notifier never ran"
            : $"notifier log (Warning+): {string.Join(" | ", logged)}");

        // WHETHER the planted key is in the captured notices, never the key itself. The absence
        // assertions in the first test are the real control on this; printing the value here would
        // put it in a public CI log for no diagnostic gain.
        var bodies = _webhook.Bodies;
        parts.Add($"notices captured: {bodies.Count}");
        parts.Add(
            "planted upstream key present in any captured notice: "
            + bodies.Any(b => b.Contains(UpstreamKeyFragment, StringComparison.Ordinal)));

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Resolves the real gated source and searches once, swallowing whatever it throws. A
    /// permanently disabled source is SKIPPED rather than failed, so the later calls return an empty
    /// list instead of throwing — both shapes are fine here, because what is being counted is
    /// notices.
    /// </summary>
    private async Task SearchOnceAsync(WebApplicationFactoryHost host)
    {
        using var scope = host.Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<Arbitarr.Core.Sources.ISourceRegistry>();
        var sources = await registry.ResolveAsync(CancellationToken.None);

        lock (_searchGate)
        {
            _resolvedSourceCount = sources.Count;
        }

        foreach (var source in sources)
        {
            try
            {
                var results = await source.SearchAsync(
                    new Arbitarr.Core.Sources.SearchQuery(
                        "permanent.disable.probe",
                        Array.Empty<int>(),
                        Limit: 100,
                        Arbitarr.Core.Sources.SearchProtocol.Newznab),
                    CancellationToken.None);

                // A search that RETURNS rather than throws is the shape that hides a bug here: the
                // gate reads a normal return as a successful call. Recording it — and that it was
                // empty — is what separates "the 401 was misclassified" from "no 401 was ever seen".
                RecordSearchOutcome($"returned normally with {results.Count} result(s)");
            }
            catch (Exception ex)
            {
                // The 401 surfaces as the adapter's exception. The outcome has already been recorded
                // by the gate at this point, which is the only thing this helper is arranging — but
                // WHICH exception it was decides whether the 401 arrived, so it is recorded too.
                RecordSearchOutcome(DescribeSearchException(ex));
            }
        }
    }

    /// <summary>
    /// Appends one search's visible outcome to <see cref="_searchOutcomes"/>.
    /// </summary>
    private void RecordSearchOutcome(string description)
    {
        lock (_searchGate)
        {
            _searchOutcomes.Add(description);
        }
    }

    /// <summary>
    /// An exception rendered as the two facts that decide the diagnosis: its type, and the HTTP
    /// status when it carries one.
    ///
    /// <para><b>The message text is deliberately NOT included</b>, and no response body is read
    /// here. An <see cref="HttpRequestException"/> from <c>EnsureSuccessStatusCode</c> carries the
    /// status in a typed property, which is the field the classifier itself switches on — so the
    /// status is the whole diagnostic value, while the message is upstream-supplied text that on
    /// this path is built from <see cref="UpstreamRejectionBody"/> and therefore CARRIES THE PLANTED
    /// KEY. Printing it would leak that key into a public CI log (CLAUDE.md §1) and would also be
    /// the very leak the first test's absence assertions exist to catch.</para>
    /// </summary>
    private static string DescribeSearchException(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: not null } http =>
            $"threw HttpRequestException with StatusCode={(int)http.StatusCode.Value} "
            + $"{http.StatusCode.Value} (message withheld: it embeds the upstream body)",
        HttpRequestException =>
            "threw HttpRequestException with NO StatusCode, so it failed BEFORE any response "
            + "(connect/DNS), which the classifier reads as TransientFailure, not "
            + "AuthenticationFailure",
        _ => $"threw {DescribeChain(ex)}",
    };

    /// <summary>
    /// The exception CHAIN by type, with the structured fields that identify a database fault, and
    /// the top few frames — never a message.
    ///
    /// <para><b>Why types and codes rather than text.</b> "DbUpdateException" alone stops one step
    /// short of a cause: a <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c> (5/6) means contention on the
    /// file, whereas a <c>SQLITE_CONSTRAINT</c> (19) means two writers raced to INSERT the same row.
    /// Those are different bugs with different owners, and both arrive wrapped in the same outer
    /// type, so only the inner code separates them.</para>
    ///
    /// <para><b>Everything printed is a number, a type name or an enum name</b> — none is free text
    /// and none is caller- or row-supplied, so no message and no column VALUE can ride along into a
    /// public CI log (CLAUDE.md §1). Frames carry method names only, with no file paths or line
    /// numbers, so nothing here reveals a checkout layout either.</para>
    /// </summary>
    private static string DescribeChain(Exception ex)
    {
        var parts = new List<string>();

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var frame = current.GetType().Name;

            if (current is Microsoft.EntityFrameworkCore.DbUpdateException update)
            {
                if (current is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                {
                    frame += " [concurrency]";
                }

                // Entity TYPE names and their states only — never a property value, which would be
                // row data. Which entities are in one failed save is what names the racing writer.
                var entries = update.Entries
                    .Select(e => $"{e.Entity.GetType().Name}:{e.State}")
                    .ToArray();

                frame += entries.Length == 0
                    ? " (no entries)"
                    : $" (entries: {string.Join(", ", entries)})";
            }

            if (current is Microsoft.Data.Sqlite.SqliteException sqlite)
            {
                // THE FIELD THAT DECIDES THE DIAGNOSIS. 5/6 = BUSY/LOCKED (contention, the arb-tdc4
                // family); 19 = CONSTRAINT (a genuine duplicate-insert race).
                frame += $" (SqliteErrorCode={sqlite.SqliteErrorCode}, "
                    + $"SqliteExtendedErrorCode={sqlite.SqliteExtendedErrorCode})";
            }

            parts.Add(frame);
        }

        var frames = new System.Diagnostics.StackTrace(ex, fNeedFileInfo: false)
            .GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m is not null)
            .Take(6)
            .Select(m => $"{m!.DeclaringType?.Name ?? "?"}.{m.Name}")
            .ToArray();

        var chain = string.Join(" <- ", parts);

        return frames.Length == 0
            ? $"{chain} (no managed frames; messages withheld)"
            : $"{chain} at {string.Join(" <- ", frames)} (messages withheld)";
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
    private async Task<string> WaitForOneNoticeAsync(WebApplicationFactoryHost host)
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
                    : $"The notifier logged: {string.Join(" | ", logged)}")
                // This message previously stopped at "it never ran at all", which is where the other
                // test's 15s failure also stopped — silent on whether the search that should have
                // raised the notice had even reached the upstream. Both failures fit "the stub did
                // not answer", so both now report the same evidence.
                + " Diagnostics: " + await DiagnoseAsync(host));
            return string.Empty;
        }
    }
}
