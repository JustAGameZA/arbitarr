using System.Net;
using System.Text.Json;
using Arbitarr.Core.Notifications;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Notifications;
using Arbitarr.Data.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        await WaitForOneNoticeAsync();

        // Three more searches. The gate now refuses the source outright, so these are skips — and a
        // skip must not re-announce a condition the operator has already been told about.
        for (var i = 0; i < 3; i++)
        {
            await SearchOnceAsync(host);
        }

        await Task.Delay(500);
        Assert.Single(_webhook.Bodies);
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
    /// Waits for the fire-and-forget delivery to land. The notifier hands off to <c>Task.Run</c> by
    /// design — that is what keeps a webhook POST off the search path — so the test waits on the
    /// CONDITION rather than sleeping a fixed span.
    /// </summary>
    private async Task<string> WaitForOneNoticeAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var bodies = _webhook.Bodies;
            if (bodies.Count > 0)
            {
                return bodies[0];
            }

            await Task.Delay(25);
        }

        Assert.Fail("No notification was posted; the composed host may not wire the notifying gate.");
        return string.Empty;
    }
}
