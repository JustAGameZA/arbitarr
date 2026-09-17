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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

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
        IReadOnlySet<NotificationTrigger>? triggers = null)
    {
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
            _time));

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

}
