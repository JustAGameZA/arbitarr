using System.Net;
using System.Text.Json;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Notifications;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Notifications;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Notifications;
using Arbitarr.Host.Sources;
using Arbitarr.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-rx1f: one notification when a source becomes permanently disabled because upstream rejected
/// Arbitarr's API key, one when a later success clears it, and nothing at all in between.
///
/// <para><b>These drive the real <see cref="BudgetedUpstreamSource"/> against a real
/// <see cref="SourceBackoffStore"/> over real SQLite, and assert the SERIALIZED POSTED BODY.</b>
/// The edge is computed from the stored row's flag before and after the record, so a test that
/// stubbed the store would be asserting its own arithmetic rather than the product's — and the body
/// is what the operator actually reads, so a test inspecting a <c>NotificationPayload</c> would pass
/// even if serialization dropped the message.</para>
///
/// <para><b>Every silence assertion here carries a positive control in the SAME harness</b>
/// (CLAUDE.md §4): each one first drives the authentication path and asserts a notice DID arrive,
/// which proves the capture would have seen a second had one been sent. <c>Assert.Empty</c> on a
/// capture that was never wired passes for the wrong reason, and more absence assertions do not fix
/// a vacuous one.</para>
///
/// <para>Webhook URLs are obviously-fake <c>example.com</c> forms and the upstream address is RFC
/// 5737 TEST-NET-1: nothing here can reach a real endpoint.</para>
/// </summary>
public sealed class SourcePermanentDisableNotifierTests : IDisposable
{
    private const string WebhookUrl = "https://example.com/hooks/placeholder-permanent-disable-token";

    private const string SourceName = "indexer-a";

    private const string OtherSourceName = "indexer-b";

    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Far enough before <see cref="Now"/> that <see cref="SourceBackoffPolicy.StartupGraceWindow"/>
    /// has elapsed, so nothing here passes merely because escalation was suppressed.
    /// </summary>
    private static readonly DateTimeOffset StartedAt = Now - TimeSpan.FromHours(1);

    private static readonly SearchQuery Query =
        new("example", Array.Empty<int>(), Limit: 100, SearchProtocol.Newznab);

    /// <summary>
    /// How long a wait on a REAL signal is allowed to take before it is called a hang. Generous
    /// because it is never reached in a healthy run: it exists so a broken wiring fails by name
    /// rather than hanging until the test host gives up, not to paper over a slow machine.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times <see cref="ReleaseRetryPausesUntilAsync"/> advances the fake clock before
    /// declaring the background work stuck. Comfortably more than the handful of pauses any test
    /// here needs, and an iteration count rather than a duration for the reason given there.
    /// </summary>
    private const int ReleaseIterations = 200;

    private readonly SqliteTestDatabase _database = new("arr-searcher-permanent-disable-notifier-test");
    private readonly FakeTimeProvider _time = new(Now);
    private readonly CapturingHandler _handler = new();
    private ServiceProvider? _provider;

    public void Dispose()
    {
        _provider?.Dispose();
        _database.Dispose();
    }

    /// <summary>Captures the real posted bodies, so assertions read what the operator would.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Lock _sync = new();

        private readonly List<string> _bodies = [];

        private int _attempts;

        /// <summary>
        /// A snapshot, taken under the same lock the writes take. The concurrency test posts from
        /// two tasks at once, and reading a <see cref="List{T}"/> while another thread appends to it
        /// is undefined — a flake that would read as the feature double-notifying.
        /// </summary>
        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_sync)
                {
                    return _bodies.ToArray();
                }
            }
        }

        /// <summary>
        /// How many times the transport actually called out, counted BEFORE any scripted failure is
        /// raised. <see cref="Bodies"/> cannot answer this: a delivery that throws records no body,
        /// so a POST-retry regression would look like silence rather than like the duplicate it is.
        /// </summary>
        public int Attempts
        {
            get
            {
                lock (_sync)
                {
                    return _attempts;
                }
            }
        }

        /// <summary>Raised on every call instead of answering, or null to answer normally.</summary>
        public Exception? Failure { get; set; }

        /// <summary>
        /// Awaited before the call is answered, or null to answer at once. This is how a test HOLDS a
        /// delivery open: the notice is genuinely in flight for exactly as long as this source is
        /// incomplete, with no sleep deciding how long that is.
        /// </summary>
        public TaskCompletionSource? Gate { get; set; }

        /// <summary>
        /// Completed when a call has ARRIVED, before <see cref="Gate"/> is awaited. Lets a test wait
        /// until the delivery is genuinely in flight rather than merely scheduled.
        /// </summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_sync)
            {
                _attempts++;
            }

            Entered.TrySetResult();

            if (Gate is { } gate)
            {
                await gate.Task;
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            lock (_sync)
            {
                _bodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    /// <summary>
    /// A source that throws whatever it is told to on every call. The 401 is raised as the
    /// <see cref="HttpRequestException"/> with a STATUS CODE that
    /// <c>BudgetedUpstreamSource.ClassifyOutcome</c> matches on — never as message text, which is the
    /// distinction that file's comment exists to protect.
    /// </summary>
    private sealed class ScriptedSource : IUpstreamSource
    {
        private readonly string _name;

        public ScriptedSource(string name) => _name = name;

        /// <summary>Thrown on the next call, or null to answer successfully.</summary>
        public Exception? Failure { get; set; }

        public string Name => _name;

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken = default)
            => Failure is { } failure
                ? Task.FromException<IReadOnlyList<ReleaseCandidate>>(failure)
                : Task.FromResult<IReadOnlyList<ReleaseCandidate>>(Array.Empty<ReleaseCandidate>());

        public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("These tests drive the search path only.");

        public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("These tests drive the search path only.");
    }

    /// <summary>The rejection upstream answers a bad key with, matched by STATUS CODE.</summary>
    private static HttpRequestException Rejected() =>
        new("upstream said no", inner: null, HttpStatusCode.Unauthorized);

    private static Source CreateConfiguration(string displayName) => new()
    {
        Kind = "Newznab",
        DisplayName = displayName,
        // RFC 5737 TEST-NET-1: non-routable, so nothing here can leave the process.
        BaseUrl = "http://192.0.2.50:9117",
        ApiPath = "/api",
        LimitsUnit = "Day",
    };

    /// <summary>
    /// A provider wired the way Program.cs wires the gate: SCOPED stores over the shared database, a
    /// SINGLETON scope factory, and a SINGLETON notifier. The scoping is what the notify-once
    /// property rests on, so it is the real thing rather than a hand-built graph.
    /// </summary>
    private async Task<ServiceProvider> BuildProviderAsync(
        bool enabled = true,
        IReadOnlySet<NotificationTrigger>? triggers = null,
        CapturingLoggerProvider? logs = null,
        Exception? postFailure = null)
    {
        _handler.Failure = postFailure;

        var services = new ServiceCollection();
        services.AddDbContext<ArbitarrDbContext>(options => options.UseSqlite(_database.ConnectionString));
        services.AddSingleton<TimeProvider>(_time);
        services.AddScoped(sp => new NotificationRepository(sp.GetRequiredService<ArbitarrDbContext>(), _time));
        services.AddSingleton(new WebhookNotificationTransport(new HttpClient(_handler)));
        services.AddScoped(sp => new SourceApiHitCounter(
            sp.GetRequiredService<ArbitarrDbContext>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new SourceBackoffStore(
            sp.GetRequiredService<ArbitarrDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            StartedAt));
        services.AddSingleton<ISourceGateScopeFactory, SourceGateScopeFactory>();
        services.AddSingleton<SourcePermanentDisableNotifier>(sp => new SourcePermanentDisableNotifier(
            sp.GetRequiredService<IServiceScopeFactory>(),
            _time,
            // The capturing sink when a test asked for one, so the retry tests read the notifier's
            // own account of what it did rather than inferring it from a body count.
            logs is null
                ? null
                : new LoggerFactory([logs]).CreateLogger<SourcePermanentDisableNotifier>()));

        var provider = services.BuildServiceProvider();
        _provider = provider;

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
            await context.Database.MigrateAsync();

            await scope.ServiceProvider.GetRequiredService<NotificationRepository>().SetSettingsAsync(
                new NotificationSettings(
                    Enabled: enabled,
                    ConsecutiveFailureThreshold: 3,
                    SuppressionRateThreshold: 0.5,
                    SuppressionRateWindow: TimeSpan.FromHours(1),
                    EnabledTriggers: triggers ?? NotificationSettings.AllTriggers),
                WebhookUrl,
                CancellationToken.None);
        }

        return provider;
    }

    private static BudgetedUpstreamSource Gate(ServiceProvider provider, ScriptedSource inner) =>
        new(
            inner,
            CreateConfiguration(inner.Name),
            provider.GetRequiredService<ISourceGateScopeFactory>(),
            NullEventSink.Instance,
            provider.GetRequiredService<SourcePermanentDisableNotifier>());

    /// <summary>
    /// Drives one search and lets the fire-and-forget delivery land. The notifier hands off to
    /// <c>Task.Run</c> by design — that is what keeps a webhook off the search path — so the test has
    /// to wait for the POST rather than for the search, and waiting on a CONDITION (a body count)
    /// rather than sleeping a fixed span is what keeps it from being timing-dependent in either
    /// direction.
    /// </summary>
    private async Task SearchAsync(BudgetedUpstreamSource gated, bool expectFailure)
    {
        if (expectFailure)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => gated.SearchAsync(Query));
        }
        else
        {
            await gated.SearchAsync(Query);
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> bodies have been posted, or fails. Used only
    /// where a notice is EXPECTED; the silence assertions instead settle with
    /// <see cref="SettleAsync"/>, which cannot pass early.
    /// </summary>
    private async Task<IReadOnlyList<string>> WaitForBodiesAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var bodies = _handler.Bodies;
            if (bodies.Count >= count)
            {
                return bodies;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Expected at least {count} notification(s); saw {_handler.Bodies.Count}.");
        return Array.Empty<string>();
    }

    /// <summary>
    /// Gives any notification that WAS going to be sent time to arrive before a count is asserted.
    /// A silence assertion taken the instant the search returns would pass against a real duplicate
    /// simply because the background delivery had not run yet — which is the vacuous shape §4 bans.
    /// </summary>
    private static Task SettleAsync() => Task.Delay(300);

    private static string MessageOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("message").GetString() ?? string.Empty;

    private static string TriggerOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("trigger").GetString() ?? string.Empty;

    [Fact]
    public async Task The_first_authentication_failure_posts_one_notice_naming_the_source()
    {
        var provider = await BuildProviderAsync();
        var inner = new ScriptedSource(SourceName) { Failure = Rejected() };
        var gated = Gate(provider, inner);

        await SearchAsync(gated, expectFailure: true);

        var body = Assert.Single(await WaitForBodiesAsync(1));
        Assert.Equal("sourcePermanentlyDisabled", TriggerOf(body));

        var message = MessageOf(body);
        Assert.Equal(
            SourcePermanentDisableNotifier.Summarize(SourceName, SourcePermanentDisableTransition.Appeared),
            message);
        Assert.Contains(SourceName, message, StringComparison.Ordinal);

        // The notice does not promise a recovery path: nothing currently re-enables a permanently
        // disabled source once its key is corrected (arb-fllv), so it states only what happened.
        Assert.Contains("is disabled. Searches skip it.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeat_authentication_failures_while_already_disabled_post_nothing_further()
    {
        var provider = await BuildProviderAsync();
        var inner = new ScriptedSource(SourceName) { Failure = Rejected() };
        var gated = Gate(provider, inner);

        // POSITIVE CONTROL, in this same harness: the first failure DOES notify, which proves the
        // capture is wired and would have seen any further notice below.
        await SearchAsync(gated, expectFailure: true);
        Assert.Single(await WaitForBodiesAsync(1));

        // The gate now refuses the source outright (IsCallableAsync sees the permanent flag), so
        // these are skips rather than calls — which is itself the point: neither a skip nor a repeat
        // failure may re-announce a condition the operator has already been told about.
        for (var i = 0; i < 4; i++)
        {
            await gated.SearchAsync(Query);
        }

        await SettleAsync();
        Assert.Single(_handler.Bodies);
    }

    [Fact]
    public async Task A_second_authentication_failure_that_does_reach_upstream_posts_nothing_further()
    {
        var provider = await BuildProviderAsync();
        var notifier = provider.GetRequiredService<SourcePermanentDisableNotifier>();
        var scopeFactory = provider.GetRequiredService<ISourceGateScopeFactory>();

        // POSITIVE CONTROL, same harness: the first record DOES notify.
        await notifier.RecordAndNotifyAsync(SourceName, SourceCallOutcome.AuthenticationFailure, scopeFactory);
        Assert.Single(await WaitForBodiesAsync(1));

        // The same outcome again, driven at the seam rather than through a search — the gate refuses
        // a disabled source, so a search can only ever reach the skip path and could never exercise
        // the flag-already-true arm this test exists for.
        for (var i = 0; i < 3; i++)
        {
            await notifier.RecordAndNotifyAsync(SourceName, SourceCallOutcome.AuthenticationFailure, scopeFactory);
        }

        await SettleAsync();
        Assert.Single(_handler.Bodies);
    }

    [Fact]
    public async Task A_success_after_a_disable_posts_the_clearing_notice_once()
    {
        var provider = await BuildProviderAsync();
        var notifier = provider.GetRequiredService<SourcePermanentDisableNotifier>();
        var scopeFactory = provider.GetRequiredService<ISourceGateScopeFactory>();

        await notifier.RecordAndNotifyAsync(SourceName, SourceCallOutcome.AuthenticationFailure, scopeFactory);
        Assert.Single(await WaitForBodiesAsync(1));

        await notifier.RecordAndNotifyAsync(SourceName, SourceCallOutcome.Success, scopeFactory);

        var bodies = await WaitForBodiesAsync(2);
        Assert.Equal(2, bodies.Count);
        Assert.Equal("sourcePermanentlyDisabledCleared", TriggerOf(bodies[1]));

        var message = MessageOf(bodies[1]);
        Assert.Equal(
            SourcePermanentDisableNotifier.Summarize(SourceName, SourcePermanentDisableTransition.Cleared),
            message);
        Assert.Contains("active again", message, StringComparison.Ordinal);

        // A SECOND success is not a second edge. The positive control for this silence is the pair of
        // notices already captured above, in this same harness.
        await notifier.RecordAndNotifyAsync(SourceName, SourceCallOutcome.Success, scopeFactory);
        await SettleAsync();
        Assert.Equal(2, _handler.Bodies.Count);
    }

    [Fact]
    public async Task A_success_by_a_source_that_was_never_disabled_posts_nothing()
    {
        var provider = await BuildProviderAsync();
        var healthy = Gate(provider, new ScriptedSource(OtherSourceName));

        await healthy.SearchAsync(Query);
        await healthy.SearchAsync(Query);
        await SettleAsync();
        Assert.Empty(_handler.Bodies);

        // POSITIVE CONTROL: the same harness, the same capture, a source that IS rejected — so the
        // emptiness above is a real silence rather than a capture that was never reached.
        var rejected = Gate(provider, new ScriptedSource(SourceName) { Failure = Rejected() });
        await SearchAsync(rejected, expectFailure: true);
        Assert.Single(await WaitForBodiesAsync(1));
    }

    [Fact]
    public async Task A_transient_failure_posts_nothing()
    {
        var provider = await BuildProviderAsync();
        var flaky = Gate(provider, new ScriptedSource(OtherSourceName) { Failure = new TimeoutException("slow") });

        await Assert.ThrowsAsync<TimeoutException>(() => flaky.SearchAsync(Query));
        await SettleAsync();
        Assert.Empty(_handler.Bodies);

        // POSITIVE CONTROL in the same harness.
        var rejected = Gate(provider, new ScriptedSource(SourceName) { Failure = Rejected() });
        await SearchAsync(rejected, expectFailure: true);
        Assert.Single(await WaitForBodiesAsync(1));
    }

    [Fact]
    public async Task A_refusal_Arbitarr_itself_issued_posts_nothing()
    {
        var provider = await BuildProviderAsync();

        // SourceUnavailableException classifies as NotAttempted, which writes no row at all. It must
        // neither disable (it is not upstream's verdict) nor clear (an open breaker must not
        // re-enable a source whose key is rejected) — see ClassifyOutcome's comment.
        var refused = Gate(
            provider,
            new ScriptedSource(OtherSourceName) { Failure = new SourceUnavailableException("breaker open") });

        await Assert.ThrowsAsync<SourceUnavailableException>(() => refused.SearchAsync(Query));
        await SettleAsync();
        Assert.Empty(_handler.Bodies);

        // POSITIVE CONTROL in the same harness.
        var rejected = Gate(provider, new ScriptedSource(SourceName) { Failure = Rejected() });
        await SearchAsync(rejected, expectFailure: true);
        Assert.Single(await WaitForBodiesAsync(1));
    }

    [Fact]
    public async Task A_cancelled_search_posts_nothing()
    {
        var provider = await BuildProviderAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var cancelled = Gate(
            provider,
            new ScriptedSource(OtherSourceName) { Failure = new OperationCanceledException(cts.Token) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.SearchAsync(Query, cts.Token));
        await SettleAsync();
        Assert.Empty(_handler.Bodies);

        // POSITIVE CONTROL in the same harness.
        var rejected = Gate(provider, new ScriptedSource(SourceName) { Failure = Rejected() });
        await SearchAsync(rejected, expectFailure: true);
        Assert.Single(await WaitForBodiesAsync(1));
    }

    [Fact]
    public async Task Disabling_one_source_notifies_for_that_source_only_and_the_other_notifies_separately()
    {
        var provider = await BuildProviderAsync();
        var a = Gate(provider, new ScriptedSource(SourceName) { Failure = Rejected() });
        var b = Gate(provider, new ScriptedSource(OtherSourceName));

        await SearchAsync(a, expectFailure: true);
        var first = await WaitForBodiesAsync(1);

        // Exactly one notice, and it names A. B is healthy and must not appear anywhere in it — a
        // per-source condition announced against the wrong source is worse than no notice.
        Assert.Single(first);
        Assert.Contains(SourceName, MessageOf(first[0]), StringComparison.Ordinal);
        Assert.DoesNotContain(OtherSourceName, MessageOf(first[0]), StringComparison.Ordinal);

        // B searching successfully while A is disabled adds nothing: B was never disabled.
        await b.SearchAsync(Query);
        await SettleAsync();
        Assert.Single(_handler.Bodies);

        // Now B is rejected too: a SECOND, DISTINCT notice naming B.
        var rejectedB = Gate(provider, new ScriptedSource(OtherSourceName) { Failure = Rejected() });
        await SearchAsync(rejectedB, expectFailure: true);

        var bodies = await WaitForBodiesAsync(2);
        Assert.Equal(2, bodies.Count);
        Assert.Contains(OtherSourceName, MessageOf(bodies[1]), StringComparison.Ordinal);
        Assert.NotEqual(MessageOf(bodies[0]), MessageOf(bodies[1]));
    }

    [Fact]
    public async Task A_restart_finding_the_flag_already_set_posts_nothing()
    {
        var provider = await BuildProviderAsync();

        // Write the disabled state the way a PREVIOUS process would have left it, with no notifier
        // in play — so nothing about this arrangement can have raised an edge.
        using (var scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SourceBackoffStore>()
                .RecordOutcomeAsync(SourceName, SourceCallOutcome.AuthenticationFailure, CancellationToken.None);
        }

        Assert.Empty(_handler.Bodies);

        // A FRESH notifier and a fresh gate, standing in for the new process. The before-state comes
        // off the durable row, so it reads true and there is no edge to raise.
        var fresh = new SourcePermanentDisableNotifier(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _time);

        var scopeFactory = provider.GetRequiredService<ISourceGateScopeFactory>();

        await fresh.RecordAndNotifyAsync(SourceName, SourceCallOutcome.AuthenticationFailure, scopeFactory);
        await SettleAsync();
        Assert.Empty(_handler.Bodies);

        // POSITIVE CONTROL: the SAME fresh notifier over a source it has not seen disabled does
        // notify, so the silence above is the flag being read and not a notifier that never works.
        await fresh.RecordAndNotifyAsync(OtherSourceName, SourceCallOutcome.AuthenticationFailure, scopeFactory);
        Assert.Single(await WaitForBodiesAsync(1));
    }

    [Fact]
    public async Task Two_concurrent_authentication_failures_for_one_source_post_exactly_one_notice()
    {
        var provider = await BuildProviderAsync();
        var notifier = provider.GetRequiredService<SourcePermanentDisableNotifier>();
        var scopeFactory = provider.GetRequiredService<ISourceGateScopeFactory>();

        // Two records for ONE source launched together, each through the REAL scope factory so each
        // opens its own DI scope and its own DbContext — the shape UpstreamMergeStage's Task.WhenAll
        // produces. Without the per-source semaphore both would read false before the write and both
        // would announce.
        await Task.WhenAll(
            Task.Run(() => notifier.RecordAndNotifyAsync(
                SourceName,
                SourceCallOutcome.AuthenticationFailure,
                scopeFactory)),
            Task.Run(() => notifier.RecordAndNotifyAsync(
                SourceName,
                SourceCallOutcome.AuthenticationFailure,
                scopeFactory)));

        await WaitForBodiesAsync(1);
        await SettleAsync();
        Assert.Single(_handler.Bodies);
    }

    [Fact]
    public async Task Concurrent_failures_for_DIFFERENT_sources_each_get_their_own_notice()
    {
        var provider = await BuildProviderAsync();
        var notifier = provider.GetRequiredService<SourcePermanentDisableNotifier>();
        var scopeFactory = provider.GetRequiredService<ISourceGateScopeFactory>();

        // The gate is per SOURCE, so two different sources failing at the same instant must BOTH be
        // announced. A single global gate would still pass the test above and fail this one.
        await Task.WhenAll(
            Task.Run(() => notifier.RecordAndNotifyAsync(
                SourceName,
                SourceCallOutcome.AuthenticationFailure,
                scopeFactory)),
            Task.Run(() => notifier.RecordAndNotifyAsync(
                OtherSourceName,
                SourceCallOutcome.AuthenticationFailure,
                scopeFactory)));

        // Also the seam's CONCURRENT case, which the two single-delivery tests below cannot reach:
        // two deliveries are counted at once here, so this covers the count returning to zero only
        // after BOTH finish rather than after whichever completes first.
        await notifier.DeliveriesIdle.WaitAsync(HangGuard);

        var bodies = await WaitForBodiesAsync(2);
        Assert.Equal(2, bodies.Count);

        var messages = bodies.Select(MessageOf).ToArray();
        Assert.Contains(messages, m => m.Contains(SourceName, StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains(OtherSourceName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_muted_trigger_posts_nothing_and_the_other_edge_still_does()
    {
        // Only the CLEARING trigger is enabled, so the appearing edge is computed and suppressed at
        // delivery — the per-trigger gate, not a change in edge detection.
        var provider = await BuildProviderAsync(
            triggers: new HashSet<NotificationTrigger>
            {
                NotificationTrigger.SourcePermanentlyDisabledCleared,
            });

        var notifier = provider.GetRequiredService<SourcePermanentDisableNotifier>();
        var scopeFactory = provider.GetRequiredService<ISourceGateScopeFactory>();

        await notifier.RecordAndNotifyAsync(SourceName, SourceCallOutcome.AuthenticationFailure, scopeFactory);
        await SettleAsync();
        Assert.Empty(_handler.Bodies);

        // POSITIVE CONTROL: the state still transitioned, so the CLEARING edge — whose trigger IS
        // enabled — does deliver. That proves the silence above is the mute and not a missed edge.
        await notifier.RecordAndNotifyAsync(SourceName, SourceCallOutcome.Success, scopeFactory);

        var body = Assert.Single(await WaitForBodiesAsync(1));
        Assert.Equal("sourcePermanentlyDisabledCleared", TriggerOf(body));
    }

    [Fact]
    public async Task The_notice_carries_the_configured_name_and_nothing_derived_from_the_failure()
    {
        var provider = await BuildProviderAsync();

        // What upstream said. A real 401 body or header can carry the rejected key itself, so none
        // of it may reach a webhook, which posts to a third-party service off this machine.
        const string UpstreamText = "401 Unauthorized: apikey placeholder-upstream-rejected-key is invalid";

        var gated = Gate(
            provider,
            new ScriptedSource(SourceName)
            {
                Failure = new HttpRequestException(UpstreamText, inner: null, HttpStatusCode.Unauthorized),
            });

        await SearchAsync(gated, expectFailure: true);
        var body = Assert.Single(await WaitForBodiesAsync(1));

        // LEAK CONTROL FIRST. The search is shown to go RED against the message a regression WOULD
        // have produced — the upstream text interpolated in — before it is run against the real one.
        // Without this, DoesNotContain passes just as happily on a body the secret was never near.
        var leaked = SummarizeAsALeakWould(SourceName, UpstreamText);
        Assert.Contains("placeholder-upstream-rejected-key", leaked, StringComparison.Ordinal);
        Assert.Contains(UpstreamText, leaked, StringComparison.Ordinal);

        // The real notice, searched the same way, carries neither.
        Assert.DoesNotContain("placeholder-upstream-rejected-key", body, StringComparison.Ordinal);
        Assert.DoesNotContain(UpstreamText, body, StringComparison.Ordinal);

        // Nor the URL, nor the webhook target. The configured display name is all that varies.
        Assert.DoesNotContain("192.0.2.50", body, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookUrl, body, StringComparison.Ordinal);
        Assert.Contains(SourceName, MessageOf(body), StringComparison.Ordinal);
    }

    /// <summary>
    /// The message a leaking implementation WOULD have built, constructed here so the detectability
    /// control above has something to go red against WITHOUT putting vulnerable code in the product
    /// (CLAUDE.md §4). It is never called by anything but that control.
    /// </summary>
    private static string SummarizeAsALeakWould(string sourceName, string upstreamText) =>
        $"Source '{sourceName}' rejected Arbitarr's API key and is disabled: {upstreamText}";

    /// <summary>
    /// Collects every line the notifier logs, so the retry tests below assert on the notifier's own
    /// account of what it did rather than inferring it from a body count. Thread-safe because the
    /// lines are written from the background delivery task and read from the test's thread.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _sync = new();

        private readonly List<string> _lines = [];

        /// <summary>
        /// Fragments a test is waiting for, and the source completed when one arrives. Held under the
        /// same lock as <see cref="_lines"/> so a line cannot slip between a waiter checking the
        /// lines it has and registering interest in the next one.
        /// </summary>
        private readonly List<(string Fragment, TaskCompletionSource Arrived)> _waiters = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_sync)
                {
                    return _lines.ToArray();
                }
            }
        }

        /// <summary>
        /// A task completing when a line containing <paramref name="fragment"/> has been logged,
        /// including one already logged before this was called. That already-seen check happens
        /// UNDER THE LOCK together with the registration, which is what makes this a signal rather
        /// than a poll with a smaller window: there is no instant at which the line has arrived and
        /// the waiter is neither satisfied nor registered.
        /// </summary>
        public Task Logged(string fragment)
        {
            lock (_sync)
            {
                if (_lines.Any(line => line.Contains(fragment, StringComparison.Ordinal)))
                {
                    return Task.CompletedTask;
                }

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((fragment, waiter));
                return waiter.Task;
            }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private void Add(string line)
        {
            List<TaskCompletionSource> satisfied = [];

            lock (_sync)
            {
                _lines.Add(line);

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (line.Contains(_waiters[i].Fragment, StringComparison.Ordinal))
                    {
                        satisfied.Add(_waiters[i].Arrived);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            // Outside the lock: a continuation must never run while the logging path still holds the
            // gate its own next line needs.
            foreach (var waiter in satisfied)
            {
                waiter.TrySetResult();
            }
        }

        private sealed class Sink : ILogger
        {
            private readonly CapturingLoggerProvider _owner;

            public Sink(CapturingLoggerProvider owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _owner.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    /// <summary>
    /// Makes the settings read fail by RENAMING the table out from under the reader, and puts it
    /// back.
    ///
    /// <para><b>Why the real database rather than a substituted repository.</b>
    /// <c>NotificationRepository</c> is sealed with non-virtual members and has no interface, so
    /// there is nothing to stub without widening production to suit a test. Renaming the table makes
    /// the REAL repository throw the REAL <see cref="SqliteException"/> the retry exists to catch, so
    /// what is exercised is the production read path and the production catch rather than a mock's
    /// idea of them. A stub could also drift from what SQLite actually throws, which is the specific
    /// way this assertion would quietly stop testing anything.</para>
    /// </summary>
    private void BreakSettingsTable() => ExecuteSql("ALTER TABLE Settings RENAME TO Settings_Broken;");

    private void RepairSettingsTable() => ExecuteSql("ALTER TABLE Settings_Broken RENAME TO Settings;");

    private void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection(_database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Releases the notifier's retry pauses, which are taken through <see cref="TimeProvider"/> and
    /// so do not elapse on their own under <see cref="FakeTimeProvider"/>.
    ///
    /// <para><b>It advances REPEATEDLY, and that loop is genuinely required rather than a poll in
    /// disguise.</b> The timer registration RACES the advance: a pause the background task has not
    /// reached yet cannot be released, because advancing the clock before the task registers its
    /// timer does nothing, and the task then waits for a time that has already gone by. The test
    /// cannot observe how far the task has got, so the only reliable move is to keep advancing until
    /// the caller's condition holds. What is waited ON is that condition, which is a real signal at
    /// every call site below.</para>
    ///
    /// <para>Bounded by an ITERATION COUNT, not a wall clock. A test that owns a
    /// <see cref="FakeTimeProvider"/> has no business reading <see cref="DateTime"/>.<c>UtcNow</c> —
    /// the point of the fake is that time here is what the test says it is — and an iteration bound
    /// also keeps the guard independent of how loaded the machine is.</para>
    /// </summary>
    private async Task ReleaseRetryPausesUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < ReleaseIterations; i++)
        {
            if (done())
            {
                return;
            }

            _time.Advance(SourcePermanentDisableNotifier.ReadRetryDelay);

            // Yielding so the background task can run and register its NEXT timer before the next
            // advance, not waiting for a span to elapse.
            await Task.Delay(1);
        }

        Assert.Fail(
            "The notifier's background work did not finish while its retry pauses were being released.");
    }

    /// <summary>
    /// Waits for a line the notifier writes from its BACKGROUND task, ON THE SINK'S OWN SIGNAL, so a
    /// test never has to guess how far that task has got. <see cref="HangGuard"/> bounds it only so a
    /// wiring break fails BY NAME instead of hanging, and the failure carries every line seen so it
    /// says which stage was reached rather than only that nothing happened.
    /// </summary>
    private static async Task WaitForLogAsync(CapturingLoggerProvider logs, string fragment)
    {
        try
        {
            await logs.Logged(fragment).WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"No log line containing '{fragment}' arrived. Lines seen: {string.Join(" | ", logs.Lines)}");
        }
    }

    /// <summary>
    /// arb-rx1f review fixup: a settings read that fails ONCE is retried, and the notice still
    /// arrives. Without the retry this posted nothing and the operator was never told the source had
    /// died — permanently, because the disable flag is committed before the delivery runs and no
    /// later search recomputes the edge.
    /// </summary>
    [Fact]
    public async Task A_transient_settings_read_failure_is_retried_and_the_notice_still_arrives()
    {
        var logs = new CapturingLoggerProvider();
        var provider = await BuildProviderAsync(logs: logs);

        BreakSettingsTable();

        var delivery = provider.GetRequiredService<SourcePermanentDisableNotifier>().NotifyAsync(
            SourceName,
            SourcePermanentDisableTransition.Appeared);

        // Repair only once the first attempt has demonstrably failed, so attempt two is the one that
        // succeeds. That ordering is what makes this the one-transient-fault case rather than a run
        // where the break never took effect.
        await WaitForLogAsync(logs, "attempt 1");
        RepairSettingsTable();
        await ReleaseRetryPausesUntilAsync(() => delivery.IsCompleted);

        await delivery;

        var body = Assert.Single(_handler.Bodies);
        Assert.Contains(SourceName, MessageOf(body), StringComparison.Ordinal);

        // It RETRIED rather than merely happening to work: the warning proves the first read failed,
        // so this cannot pass against an implementation that never had to recover from anything.
        Assert.Contains(logs.Lines, line => line.Contains("attempt 1", StringComparison.Ordinal));

        // THE RETRY WARNING MUST NOT LEAK EITHER, and this is the only test that can prove it: the
        // warning fires HERE (the assertion above is its positive control, and the posted body below
        // proves the run went on to resolve the real source and the real URL), so the values are
        // genuinely in play when their absence is asserted. The sustained-failure test cannot make
        // this assertion bite, because there the URL is never read at all.
        //
        // A mutation that appended the source name to this warning survived every other test in this
        // file, which is precisely why the assertion belongs here rather than being assumed covered.
        var retryWarnings = logs.Lines
            .Where(line => line.Contains("Reading notification settings", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(retryWarnings);
        Assert.DoesNotContain(retryWarnings, line => line.Contains(WebhookUrl, StringComparison.Ordinal));
        Assert.DoesNotContain(retryWarnings, line => line.Contains(SourceName, StringComparison.Ordinal));
        Assert.DoesNotContain(retryWarnings, line => line.Contains("192.0.2.50", StringComparison.Ordinal));
    }

    /// <summary>
    /// A sustained fault still loses the notice — the residual the fixup knowingly accepts — but it
    /// is BOUNDED and REPORTED: exactly <see cref="SourcePermanentDisableNotifier.ReadAttempts"/>
    /// attempts, then the backstop log. A silent loss is what this change removes; an unbounded retry
    /// chain holding a thread-pool thread is what it must not introduce in its place.
    /// </summary>
    [Fact]
    public async Task A_sustained_settings_read_failure_stops_after_the_bounded_attempts_and_is_logged()
    {
        var logs = new CapturingLoggerProvider();
        var provider = await BuildProviderAsync(logs: logs);

        BreakSettingsTable();

        // NotifyInBackground rather than NotifyAsync: the backstop catch being asserted lives there,
        // and driving the awaitable directly would step over the very handler under test.
        provider.GetRequiredService<SourcePermanentDisableNotifier>().NotifyInBackground(
            SourceName,
            SourcePermanentDisableTransition.Appeared);

        // The terminal signal is the backstop error, which only fires once every attempt is spent.
        await ReleaseRetryPausesUntilAsync(
            () => logs.Lines.Any(line => line.StartsWith("Error:", StringComparison.Ordinal)));

        var warnings = logs.Lines
            .Where(line => line.Contains("Reading notification settings", StringComparison.Ordinal))
            .ToArray();

        // Three TOTAL attempts means two retry warnings: the third failure is terminal and is
        // reported by the backstop instead. These warnings are also the POSITIVE CONTROL for the
        // silence below — they prove the run REACHED the read and failed it, so the empty body list
        // means the delivery was abandoned rather than never started. Assert.Empty on its own would
        // pass just as happily against a notifier that never ran at all (CLAUDE.md §4).
        Assert.Equal(SourcePermanentDisableNotifier.ReadAttempts - 1, warnings.Length);
        Assert.Contains(logs.Lines, line => line.StartsWith("Error:", StringComparison.Ordinal));

        Assert.Empty(_handler.Bodies);

        // NO SECRET-ABSENCE ASSERTION HERE, DELIBERATELY. Checking that these log lines carry no
        // webhook URL would be VACUOUS BY CONSTRUCTION (CLAUDE.md §4): the read that would have
        // produced the URL is the very thing that failed, so the notifier never held the value and
        // an empty set contains nothing. Such an assertion would pass against a genuinely leaking
        // build and prove only that this test never put the secret in play. The leak assertion that
        // CAN bite lives in A_failing_post_is_attempted_once_and_never_retried, where both reads
        // succeeded and the URL demonstrably reached the transport.
        RepairSettingsTable();
    }

    /// <summary>
    /// The POST is NOT retried. A re-POST is indistinguishable from a first POST at the receiving
    /// end, so retrying it would risk telling an operator twice that a source died — the duplicate
    /// this type exists to prevent. One failing delivery must therefore produce exactly ONE attempt,
    /// not <see cref="SourcePermanentDisableNotifier.ReadAttempts"/>.
    /// </summary>
    [Fact]
    public async Task A_failing_post_is_attempted_once_and_never_retried()
    {
        var logs = new CapturingLoggerProvider();
        var provider = await BuildProviderAsync(
            logs: logs,
            postFailure: new HttpRequestException("webhook refused"));

        provider.GetRequiredService<SourcePermanentDisableNotifier>().NotifyInBackground(
            SourceName,
            SourcePermanentDisableTransition.Appeared);

        // The transport CATCHES the failure and reports it as an outcome rather than letting it
        // propagate, so the terminal signal here is the delivery warning, not the backstop error.
        await ReleaseRetryPausesUntilAsync(
            () => logs.Lines.Any(line => line.Contains("was not delivered", StringComparison.Ordinal)));

        // POSITIVE CONTROL: the handler was reached, so this is a count of real attempts rather than
        // of a delivery that never started — the way this assertion would otherwise go vacuous.
        Assert.Equal(1, _handler.Attempts);

        // And no read-retry warning fired: a POST failure must not be mistaken for a read fault and
        // funnelled into the retry loop.
        Assert.DoesNotContain(
            logs.Lines,
            line => line.Contains("Reading notification settings", StringComparison.Ordinal));

        // LEAK ASSERTIONS, AND THIS IS THE ONE PLACE THEY BITE. Both settings reads SUCCEEDED here,
        // so the notifier held the real webhook URL and the real source configuration and then hit a
        // delivery failure — the exact moment an implementation is most tempted to log "couldn't
        // reach <url>". The sustained-read-failure test cannot make this assertion mean anything,
        // because there the URL was never read (see the note there).
        //
        // POSITIVE CONTROL FIRST (CLAUDE.md §4): the values are shown to be in play before their
        // absence is asserted. The delivery warning proves the transport was reached, which it can
        // only be with a URL in hand, and the posted payload proves the source configuration was
        // resolved.
        Assert.Contains(logs.Lines, line => line.Contains("was not delivered", StringComparison.Ordinal));
        Assert.Equal(1, _handler.Attempts);

        // Only now do the misses mean anything.
        Assert.DoesNotContain(logs.Lines, line => line.Contains(WebhookUrl, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("192.0.2.50", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(SourceName, StringComparison.Ordinal));
    }

    /// <summary>
    /// <see cref="SourcePermanentDisableNotifier.DeliveriesIdle"/> does not complete while a delivery
    /// is still running, and does once it finishes.
    ///
    /// <para>This is the property the end-to-end tests depend on. If the seam completed early they
    /// would read the capture before the POST landed and fail exactly as they did before it existed —
    /// so "not yet complete" is asserted at a moment the delivery is PROVABLY in flight (the handler
    /// has been entered and is being held), not merely soon after the call.</para>
    ///
    /// <para>No delay synchronises anything here: the handler signals its own arrival, and the
    /// release is a <see cref="TaskCompletionSource"/> the test completes.</para>
    /// </summary>
    [Fact]
    public async Task The_idle_seam_completes_only_once_a_held_delivery_is_released()
    {
        var provider = await BuildProviderAsync();
        var notifier = provider.GetRequiredService<SourcePermanentDisableNotifier>();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Gate = release;

        notifier.NotifyInBackground(SourceName, SourcePermanentDisableTransition.Appeared);

        var idle = notifier.DeliveriesIdle;

        // The delivery is now genuinely in flight: the handler has been entered and is blocked on the
        // gate. Anything asserted about the seam from here is about a real in-flight delivery.
        await _handler.Entered.Task.WaitAsync(HangGuard);

        Assert.False(idle.IsCompleted);

        // Releasing the held delivery is the ONLY thing that changes, so a completion after this can
        // be attributed to the delivery finishing and to nothing else.
        release.SetResult();

        await idle.WaitAsync(HangGuard);

        // POSITIVE CONTROL: the notice really was posted, so the seam tracked a delivery that
        // happened rather than completing because nothing was ever raised.
        Assert.Single(_handler.Bodies);
    }

    /// <summary>
    /// The seam is already complete when nothing is in flight, so awaiting it costs a caller nothing
    /// and cannot hang a test that raised no notice.
    ///
    /// <para>Asserted on the SYNCHRONOUS state rather than by awaiting: awaiting proves only that it
    /// completed eventually, which a seam that waited for a timer would also satisfy.</para>
    /// </summary>
    [Fact]
    public async Task The_idle_seam_is_already_complete_when_no_delivery_is_in_flight()
    {
        var provider = await BuildProviderAsync();
        var notifier = provider.GetRequiredService<SourcePermanentDisableNotifier>();

        Assert.True(notifier.DeliveriesIdle.IsCompleted);

        // And it returns to that state after a delivery rather than staying completed from the first
        // observation — the seam is reusable, which the end-to-end tests rely on when they await it
        // once for the notice and again for the silence check.
        notifier.NotifyInBackground(SourceName, SourcePermanentDisableTransition.Appeared);
        await notifier.DeliveriesIdle.WaitAsync(HangGuard);

        Assert.Single(_handler.Bodies);
        Assert.True(notifier.DeliveriesIdle.IsCompleted);
    }
}
