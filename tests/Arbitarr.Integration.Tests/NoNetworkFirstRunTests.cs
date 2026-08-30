using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-4 (AC21/R6): a fresh install must come up and serve successfully even with zero network
/// access — no upstream indexer reachable, no dataset ever fetched. This drives the real Host
/// composition root (<c>Program.cs</c>, unmodified) against a brand-new config directory (so
/// nothing is cached on disk) with the sole configured upstream (<see cref="Arbitarr.Sources.NzbHydra.NzbHydraSource"/>)
/// pointed at an address that refuses every connection, and asserts:
///   - startup itself never touches the network (<c>DatasetProvisioner.EnsureProvisioned</c> only
///     scaffolds directories - AC21's other half, that no dataset is ever baked into the image, is
///     covered separately at the image-content level);
///   - a lite read-only route (<c>/health</c>) still answers 200;
///   - a real Torznab search against the unreachable upstream still answers 200 with well-formed,
///     empty-but-valid search-results XML rather than a 5xx - <see cref="Arbitarr.Api.Search.UpstreamMergeStage"/>
///     already swallows any per-source failure (connection refused included) and unions in whatever
///     did respond, which for a single, totally unreachable source is simply "nothing".
/// </summary>
public sealed class NoNetworkFirstRunTests : IDisposable
{
    // RFC 5737 TEST-NET-1: guaranteed non-routable, so this connection attempt fails fast with
    // "connection refused/unreachable" rather than hanging on a real network timeout.
    private const string UnreachableBaseUrl = "http://192.0.2.1:1";
    private const string ClientApiKey = "no-network-first-run-key";

    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-m7-4-no-network-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Fresh_install_with_no_network_access_still_boots_and_serves()
    {
        Assert.False(Directory.Exists(_configDirectory));

        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", _configDirectory);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:Sources:NzbHydra:BaseUrl", UnreachableBaseUrl);
            builder.UseSetting("Arbitarr:Sources:NzbHydra:ApiKey", "irrelevant-upstream-key");
            builder.UseSetting("Arbitarr:ApiKey", ClientApiKey);
        });

        using var client = factory.CreateClient();

        // Startup itself (DatasetProvisioner.EnsureProvisioned + Database.Migrate()) must succeed
        // with no network access - a lite route answering 200 proves the Host is actually up.
        var healthResponse = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        // A real search against the sole, totally unreachable upstream must degrade to an empty
        // result set rather than surface as a 5xx - fresh installs must never look "broken" before
        // any source has ever been configured/reachable.
        var searchResponse = await client.GetAsync(
            $"/torznab/api?t=search&q=no.network.first.run&apikey={Uri.EscapeDataString(ClientApiKey)}");

        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        var body = await searchResponse.Content.ReadAsStringAsync();
        Assert.Contains("<rss", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<item", body, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", null);

        try
        {
            if (Directory.Exists(_configDirectory))
            {
                Directory.Delete(_configDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked SQLite file on Windows shouldn't fail the test run.
        }
    }
}
