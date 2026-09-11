using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Arbitarr.Core.Notifications;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-apj end to end against the REAL host composition: a refused redirect driven through the real
/// <c>/download/{proxyGuid}</c> route, reaching the real <c>NotifyingDownloadRefusalTracker</c> that
/// <c>Program.cs</c> registers, the real <c>DownloadRefusalNotifier</c>, and the real
/// <see cref="WebhookNotificationTransport"/>. Only the transport's HTTP handler is replaced, so
/// what is asserted is the body the operator's webhook would actually have received.
///
/// <para><b>Why it drives the ROUTE rather than the endpoint method.</b> CLAUDE.md §4: #57's webhook
/// test passed with a real leak because it drove an endpoint that bypassed the dispatcher. Calling
/// <c>DownloadProxyEndpoint.HandleAsync</c> with a hand-built tracker would prove the endpoint calls
/// a tracker and nothing about whether the composed application wires a NOTIFYING one — which is the
/// entire feature. An HTTP request through the host is the only shape that can tell the difference,
/// and a plain <c>DownloadRefusalTracker</c> in <c>Program.cs</c> fails this file.</para>
///
/// <para><b>The Location-header assertion carries a detectability control.</b> Asserting the notice
/// does not contain a planted redirect target proves nothing unless that search is first shown to go
/// RED against a string of the same shape that does carry it — an empty set contains nothing. The
/// control is built by <see cref="SummarizeAsALeakWould"/>, which constructs the message a
/// regression WOULD have produced (the upstream exception text interpolated in) rather than planting
/// a literal, so no vulnerable code enters the product to provide it.</para>
///
/// <para>The upstream address is RFC 5737 TEST-NET-1 and the webhook URL an obviously-fake
/// <c>example.com</c> form: no real endpoint enters committed content.</para>
/// </summary>
public sealed class DownloadRefusalNotificationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ApiKey = "placeholder-refusal-notification-client-key";

    private const string SourceName = "refusal-notification-fake-source";

    private const string WebhookUrl = "https://example.com/hooks/placeholder-refusal-notify-token";

    /// <summary>
    /// The upstream-supplied value a leak would carry. This is what a real NZBHydra2 redirect puts
    /// in its <c>Location</c> header: the indexer's own URL, with its API key in the query. It must
    /// reach no notification body, because a webhook notice travels off this machine to a third-party
    /// service the operator configured.
    /// </summary>
    private const string LocationHeaderValue =
        "http://192.0.2.99:9117/dl/indexer?apikey=placeholder-upstream-indexer-key&file=probe";

    /// <summary>The key fragment alone, so a leak of only the query still fails.</summary>
    private const string UpstreamKeyFragment = "placeholder-upstream-indexer-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly CapturingHandler _handler = new();

    public DownloadRefusalNotificationTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-refusal-notification-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", configDirectory);
            builder.UseSetting("Arbitarr:ApiKey", ApiKey);

            builder.ConfigureServices(services =>
            {
                // One fake upstream that SEARCHES normally and REFUSES the download, so the search
                // mints a real proxy guid and the download then takes the refusal path. The
                // exception is constructed exactly as NzbHydraSource constructs it — from the
                // configured source name and an int status code — which is the property the
                // Location-header assertion below is really about.
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    SourceName,
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Refusal Notification Probe Release",
                            Guid = "refusal-notification-probe-1",
                            PubDate = DateTimeOffset.UtcNow,
                            Size = 123_456,
                            Link = new Uri("http://192.0.2.72:8080/getnzb/refusal-notification-probe-1"),
                            Category = new[] { 5000 },
                            Protocol = ProtocolKind.Usenet,
                        },
                    },
                    downloadException: new UpstreamRedirectRefusedException(SourceName, 302)));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());

                // The ONLY notification-path substitution: the transport's HTTP handler. The
                // transport type, the notifier, the tracker decorator and the settings gate are all
                // the real registered ones, so this records what would genuinely have been posted.
                services.RemoveAll<WebhookNotificationTransport>();
                services.AddSingleton(new WebhookNotificationTransport(new HttpClient(_handler)));
            });
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
    public async Task A_refused_download_posts_one_notice_that_names_the_source_and_carries_no_upstream_text()
    {
        using var client = _factory.CreateClient();
        await EnableNotificationsAsync();

        var downloadPath = await MintDownloadLinkAsync(client);

        using var download = await client.GetAsync(downloadPath);

        // The refusal really happened: 502 is the proxy's answer to a refused redirect. Without
        // this the notice assertions could pass for the wrong reason (or fail for one).
        Assert.Equal(HttpStatusCode.BadGateway, download.StatusCode);

        var body = await WaitForOneNoticeAsync();

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal("downloadRefused", root.GetProperty("trigger").GetString());
        Assert.Equal(SourceName, root.GetProperty("sourceName").GetString());

        var message = root.GetProperty("message").GetString();
        Assert.NotNull(message);

        // It names the CONFIGURED source, which is the point of the notice: an operator with three
        // sources must be told which one to go and fix.
        Assert.Contains(SourceName, message!, StringComparison.Ordinal);

        // DETECTABILITY CONTROL, and it must come BEFORE the absence assertions. This is the
        // message the regression would have produced — the upstream exception's text interpolated
        // into the summary, which is the obvious "make the notice more informative" change. The two
        // searches below are run against it FIRST and are required to HIT, proving each is capable
        // of finding the value at all. Only then is their miss against the real body meaningful.
        var leaked = SummarizeAsALeakWould();
        Assert.Contains(LocationHeaderValue, leaked, StringComparison.Ordinal);
        Assert.Contains(UpstreamKeyFragment, leaked, StringComparison.Ordinal);

        // The real notice carries neither, over the WHOLE posted body rather than only the message,
        // so a future field that echoed the reason string would fail here too.
        Assert.DoesNotContain(LocationHeaderValue, body, StringComparison.Ordinal);
        Assert.DoesNotContain(UpstreamKeyFragment, body, StringComparison.Ordinal);

        // Nor does the webhook URL come back out in what was posted to it.
        Assert.DoesNotContain("placeholder-refusal-notify-token", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_refusal_posts_nothing_further()
    {
        using var client = _factory.CreateClient();
        await EnableNotificationsAsync();

        var downloadPath = await MintDownloadLinkAsync(client);

        using (var first = await client.GetAsync(downloadPath))
        {
            Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        }

        // THE POSITIVE CONTROL. Asserting a count of one after the retries would pass equally well
        // if the notifier had never fired at all; pinning that the FIRST refusal did post — through
        // this same handler, in this same host — is what makes the unchanged count below mean
        // silence rather than absence.
        await WaitForOneNoticeAsync();

        // Sonarr retries a failed grab. The health item is already present, so the operator has
        // already been told and must not be told again.
        for (var i = 0; i < 3; i++)
        {
            using var retry = await client.GetAsync(downloadPath);
            Assert.Equal(HttpStatusCode.BadGateway, retry.StatusCode);
        }

        // Long enough that a second delivery in flight would have landed: the notifier posts from a
        // background task, so a bare assertion immediately after the retries could pass simply by
        // reading too early.
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        var single = Assert.Single(_handler.Bodies);
        Assert.Equal("downloadRefused", JsonDocument.Parse(single).RootElement.GetProperty("trigger").GetString());
    }

    /// <summary>
    /// Builds the summary a leaking implementation would have produced, by interpolating the
    /// upstream-supplied redirect target into the same sentence the notifier emits. This is a
    /// mutation of the projection under test, constructed HERE in the test rather than by putting a
    /// vulnerable branch in the product — CLAUDE.md §4's "prove it without putting vulnerable code in
    /// the repository".
    /// </summary>
    private static string SummarizeAsALeakWould() =>
        $"Source '{SourceName}' refused a download by redirecting instead of serving the file "
        + $"(upstream redirected to {LocationHeaderValue}).";

    /// <summary>
    /// Turns notifications on with a webhook URL configured, through the repository rather than the
    /// admin route: the gate itself is pinned by <c>AdminNotificationEndpointsTests</c>, and going
    /// direct keeps this file about the refusal path.
    /// </summary>
    private async Task EnableNotificationsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<NotificationRepository>();

        await repository.SetSettingsAsync(
            NotificationSettings.Default with { Enabled = true },
            WebhookUrl,
            CancellationToken.None);
    }

    /// <summary>
    /// Runs a real search and returns the download path from the rendered link, so the proxy guid
    /// and the embedded client key are the ones the product itself issued. A hand-built guid would
    /// not resolve, and the download would 404 before ever reaching the refusal path.
    /// </summary>
    private static async Task<string> MintDownloadLinkAsync(HttpClient client)
    {
        using var search = await client.GetAsync(
            $"/torznab/api?t=search&q=refusal.notification.probe&apikey={Uri.EscapeDataString(ApiKey)}");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);

        var feed = XDocument.Parse(await search.Content.ReadAsStringAsync());
        var link = feed.Descendants("item").Elements("link").FirstOrDefault()?.Value;

        Assert.False(string.IsNullOrWhiteSpace(link), "The search returned no item to download.");

        return new Uri(link!).PathAndQuery;
    }

    /// <summary>
    /// Waits for the notifier's background delivery to land and returns the single body posted.
    ///
    /// <para>Polled rather than slept: the notice is delivered from a fire-and-forget task (the
    /// download path must not wait on a webhook), so a fixed delay would either be flaky or slow.
    /// The timeout failing is a real finding — it means nothing was delivered at all.</para>
    /// </summary>
    private async Task<string> WaitForOneNoticeAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (_handler.Bodies.Count > 0)
            {
                return Assert.Single(_handler.Bodies);
            }

            await Task.Delay(25);
        }

        Assert.Fail("No notification was delivered for the refused download within the timeout.");
        return string.Empty;
    }
}
