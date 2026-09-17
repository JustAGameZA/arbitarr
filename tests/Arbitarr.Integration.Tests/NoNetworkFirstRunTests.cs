using System.Net;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-4 (AC21/R6): a fresh install must come up and serve even with zero network access — no
/// upstream indexer reachable, no dataset ever fetched. This drives the real Host composition root
/// (<c>Program.cs</c>, unmodified) against a brand-new config directory, so nothing is cached on
/// disk, and asserts that startup itself never touches the network
/// (<c>DatasetProvisioner.EnsureProvisioned</c> only scaffolds directories — AC21's other half,
/// that no dataset is ever baked into the image, is covered separately at the image-content level)
/// and that a lite read-only route (<c>/health</c>) still answers 200.
///
/// <para><b>arb-nus0 SPLIT THIS INTO THE TWO SCENARIOS THE ORIGINAL ASSERTION CONFLATED, and
/// superseded one of them.</b> They differ in whether a source was ever CONFIGURED, which is what
/// decides whether an empty answer is the truth or a cover-up:</para>
///
/// <para><b>Scenario 1 — nothing configured.</b> No source resolves, so no source failed either:
/// all three of <see cref="Arbitarr.Api.Search.MergeResult"/>'s failure lists are empty and the
/// search honestly has nothing to report. This answers <b>200 with an empty, well-formed rss</b>.
/// This is AC21/R6's load-bearing half — "a fresh install must never look broken before any source
/// has ever been configured" — and it is pinned by
/// <see cref="A_fresh_install_with_no_sources_configured_at_all_still_answers_200_with_an_empty_feed"/>.
/// </para>
///
/// <para><b>Scenario 2 — a source IS configured and is unreachable.</b> That is an indexer outage,
/// not an empty result set, and it now answers <b>code 900 at HTTP 5xx</b>. CONTEXT.md: "An empty
/// result set is not an error at all — just a results element with no items. An infrastructure
/// error means the pipeline failed to produce an answer at all: code 900, HTTP 5xx." The pipeline
/// did not produce an answer here; it failed to reach the only thing that could have given one.
/// </para>
///
/// <para><b>Why the old assertion is superseded rather than the AC.</b> This class previously
/// asserted 200-with-empty-rss for scenario 2 while its stated intent described scenario 1. The 200
/// is what an *arr records as "0 results", so a wholly unreachable indexer was indistinguishable
/// from one that genuinely had nothing — the silent miss that
/// <c>SearchEndpoint.InfrastructureErrorResult</c>'s remarks exist to prevent, and the reason a 5xx
/// is the useful signal: it tells the client to retry and surface the outage. AC21's intent is
/// preserved by scenario 1's test above; only the assertion that had been written against the wrong
/// scenario moved. Ruled by the owner on 2026-09-17 when arb-nus0 surfaced the conflict.</para>
/// </summary>
public sealed class NoNetworkFirstRunTests : IDisposable
{
    // RFC 5737 TEST-NET-1: guaranteed non-routable, so this connection attempt fails fast with
    // "connection refused/unreachable" rather than hanging on a real network timeout.
    private const string UnreachableBaseUrl = "http://192.0.2.1:1";
    private const string ClientApiKey = "no-network-first-run-key";

    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-m7-4-no-network-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A second config directory, so the two scenarios below never share one. Each drives its own
    /// Host against its own brand-new <c>/config</c>, which is what makes "fresh install" true for
    /// both rather than only for whichever ran first.
    /// </summary>
    private readonly string _unconfiguredConfigDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-m7-4-no-network-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// <b>SCENARIO 2.</b> The Host boots and serves with no network access, and a search against the
    /// sole CONFIGURED-but-unreachable upstream reports an infrastructure error rather than an empty
    /// result set.
    /// </summary>
    [Fact]
    public async Task Fresh_install_with_no_network_access_still_boots_and_serves()
    {
        Assert.False(Directory.Exists(_configDirectory));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Deliberately NOT created first: the precondition above is the whole scenario, and the
            // Host must provision the directory itself.
            builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);
            builder.UseSetting("Arbitarr:Sources:NzbHydra:BaseUrl", UnreachableBaseUrl);
            builder.UseSetting("Arbitarr:Sources:NzbHydra:ApiKey", "irrelevant-upstream-key");
            builder.UseSetting("Arbitarr:ApiKey", ClientApiKey);
        });

        using var client = factory.CreateClient();

        // UNCHANGED, and still the point of AC21: startup itself (DatasetProvisioner.EnsureProvisioned
        // + Database.Migrate()) must succeed with no network access. A lite route answering 200
        // proves the Host is actually up, which is what "boots and serves" means.
        var healthResponse = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        // arb-nus0 (SUPERSEDES the previous 200/empty-rss assertion here - see the class remarks). A
        // CONFIGURED source that refuses every connection is an outage, and the pipeline produced no
        // answer at all, so the honest response is the protocol's infrastructure error at 5xx.
        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q=no.network.first.run&apikey={Uri.EscapeDataString(ClientApiKey)}");

        Assert.Equal(HttpStatusCode.InternalServerError, searchResponse.StatusCode);
        var body = await searchResponse.Content.ReadAsStringAsync();

        // Still WELL-FORMED protocol XML, not a bare 500 with no body: an *arr that reads the body
        // gets a reason, and one that only reads the status still learns the search did not happen.
        Assert.Contains(
            $"<error code=\"{Arbitarr.Api.Search.SearchEndpoint.InfrastructureErrorCode}\"",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<item", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>SCENARIO 1 - AC21/R6'S LOAD-BEARING HALF.</b> A fresh install with NO source configured at
    /// all answers 200 with an empty, well-formed feed. Nothing resolved, so nothing failed: there is
    /// no outage to report, and an install that has simply not been set up yet must not present
    /// itself as broken.
    ///
    /// <para>This test is what keeps arb-nus0 from having removed a guarantee rather than narrowed
    /// one. Without it, the supersession above would leave "a fresh install never looks broken"
    /// unpinned by anything, and a later change could escalate the unconfigured case to a 5xx with no
    /// test objecting.</para>
    /// </summary>
    [Fact]
    public async Task A_fresh_install_with_no_sources_configured_at_all_still_answers_200_with_an_empty_feed()
    {
        Assert.False(Directory.Exists(_unconfiguredConfigDirectory));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", _unconfiguredConfigDirectory);
            // NO Arbitarr:Sources:NzbHydra:* settings at all - that omission IS the scenario. The
            // registry resolves an empty source set, which its own remarks call an ordinary answer
            // rather than a failure, so all three failure lists come back empty.
            builder.UseSetting("Arbitarr:ApiKey", ClientApiKey);
        });

        using var client = factory.CreateClient();

        var healthResponse = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q=no.sources.configured&apikey={Uri.EscapeDataString(ClientApiKey)}");

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var body = await searchResponse.Content.ReadAsStringAsync();
        Assert.Contains("<rss", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<item", body, StringComparison.OrdinalIgnoreCase);

        // NON-VACUITY, and the whole distinction between the two scenarios: this really is the 200
        // path and not an error element that merely happens to contain no <item>. Without this the
        // assertion above would pass against a response that had escalated exactly as scenario 2 now
        // does, which is the confusion this pair of tests exists to keep apart.
        Assert.DoesNotContain("<error", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The delete used to run bare, with no pool clear, inside an empty <c>catch (IOException)</c> -
    /// it lost to a pooled handle and said nothing (arb-gphi).
    /// <see cref="ConfigDirectoryTeardown"/> clears both databases' pools first and THROWS if the
    /// delete still fails.
    /// </summary>
    public void Dispose()
    {
        ConfigDirectoryTeardown.Delete(_configDirectory);
        ConfigDirectoryTeardown.Delete(_unconfiguredConfigDirectory);
    }
}
