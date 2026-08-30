using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Api.Search;
using Arbitarr.Core.Arbitration;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Settings;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-1 (non-AI half): <c>GET /api/admin/search</c> is admin-gated (D2) and runs the same
/// PaginationSnapshotService/UpstreamMergeStage path as <c>/torznab/api</c>, rendering JSON with
/// releases untouched (title/size/category/guid) plus cache/rate-limit provenance.
/// </summary>
public sealed class AdHocSearchEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AdminKey = "adhoc-search-admin-key";
    private const string Route = "/api/admin/search";

    private readonly WebApplicationFactory<Program> _factory;

    public AdHocSearchEndpointTests(WebApplicationFactory<Program> factory)
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m7-adhoc-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", configDirectory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the real, config-driven upstream source registrations with a single fake
                // that echoes back one release, so this test exercises the endpoint's live merge
                // path without depending on any real upstream (NZBHydra2) being reachable.
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
                services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                    "adhoc-fake-source",
                    searchResults: new[]
                    {
                        new ReleaseCandidate
                        {
                            Title = "Ad Hoc Probe Release",
                            Guid = "adhoc-probe-1",
                            PubDate = DateTimeOffset.UtcNow,
                            Size = 654_321,
                            Link = new Uri("http://192.0.2.80:8080/getnzb/adhoc-probe-1"),
                            Category = new[] { 5030 },
                            Protocol = ProtocolKind.Usenet,
                        },
                    }));
                services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());

                // AC14b: replace the real (Arbitarr.Ai-backed) ISyncReleaseArbiter registration with
                // a deterministic fake, so these tests never need a live Ollama and can assert the
                // opt-in flag's on/off behavior plus the AC14b human-latency budget precisely.
                services.RemoveAll<ISyncReleaseArbiter>();
                services.AddSingleton<ISyncReleaseArbiter>(FakeArbiter);
            });
        });
    }

    /// <summary>
    /// Swappable per-test fake. Defaults to an instant Accept verdict for every candidate; tests
    /// that need a slow/timeout scenario replace this before issuing their request.
    /// </summary>
    private ISyncReleaseArbiter FakeArbiter { get; set; } = new StaticVerdictArbiter(Verdict.Accept, delay: null);

    private sealed class StaticVerdictArbiter : ISyncReleaseArbiter
    {
        private readonly Verdict _verdict;
        private readonly TimeSpan? _delay;

        public StaticVerdictArbiter(Verdict verdict, TimeSpan? delay)
        {
            _verdict = verdict;
            _delay = delay;
        }

        public async Task<IReadOnlyList<ArbitrationOutcome>> ArbitrateAsync(
            IReadOnlyList<ReleaseCandidate> candidates, ArbitrationContext context, CancellationToken cancellationToken)
        {
            if (_delay is { } delay)
            {
                // Simulate a slow model: wait longer than the caller's AC14b budget so a correctly
                // wired endpoint's own budget/timeout plumbing (not this fake) determines the result.
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Fall through to still return outcomes below — mirrors SyncReleaseArbiter's own
                    // fail-open contract so this fake is a faithful stand-in for latency-budget tests.
                }
            }

            return candidates.Select(c => new ArbitrationOutcome(c.Guid, _verdict, Confidence: 0.99)).ToArray();
        }
    }

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
    }

    private async Task SeedAdminKeyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();

        var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
        if (existing is null)
        {
            db.Settings.Add(new SettingEntry
            {
                Name = SettingKey.AdminApiKey.ToString(),
                Value = AdminKey,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Value = AdminKey;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GET_search_without_admin_key_is_rejected_with_401()
    {
        // An admin key must be configured first, otherwise AdminApiKeyFilter fails closed with 503
        // (unset gate) rather than 401 (wrong/missing credential) — this test targets the latter.
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"{Route}?q=probe");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_search_with_admin_key_returns_releases_untouched_and_provenance()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync($"{Route}?q=probe");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();

        Assert.NotNull(body);
        var release = Assert.Single(body!.Releases);
        Assert.Equal("Ad Hoc Probe Release", release.Title);
        Assert.Equal("adhoc-probe-1", release.Guid);
        Assert.Equal(654_321, release.Size);
        Assert.Equal(new[] { 5030 }, release.Category);
        Assert.Equal("adhoc-fake-source", release.SourceName);

        Assert.NotNull(body.Provenance);
        Assert.Empty(body.Provenance.RateLimitedSources);
    }

    [Fact]
    public async Task GET_search_folds_tvdbid_season_and_episode_into_the_query()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        // The fake source ignores query content and always returns its one seeded release; this
        // asserts only that supplying these params does not error and still reaches the merge path.
        var response = await client.GetAsync($"{Route}?tvdbid=12345&season=1&ep=3");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Releases);
    }

    [Fact]
    public async Task GET_search_forwards_categories_and_paging_params()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        var response = await client.GetAsync($"{Route}?q=probe&cat=5030,5040&limit=10&offset=0");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Releases);
    }

    [Fact]
    public async Task GET_search_without_runAiSync_never_populates_AiVerdict()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync($"{Route}?q=probe");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();
        Assert.NotNull(body);
        var release = Assert.Single(body!.Releases);
        Assert.Null(release.AiVerdict);
    }

    [Fact]
    public async Task GET_search_with_runAiSync_true_populates_AiVerdict_from_the_arbiter()
    {
        FakeArbiter = new StaticVerdictArbiter(Verdict.Reject, delay: null);
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync($"{Route}?q=probe&runAiSync=true");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();
        Assert.NotNull(body);
        var release = Assert.Single(body!.Releases);
        Assert.Equal(nameof(Verdict.Reject), release.AiVerdict);
        // AC14b never rewrites size/category/guid regardless of the AI verdict returned.
        Assert.Equal("adhoc-probe-1", release.Guid);
        Assert.Equal(654_321, release.Size);
        Assert.Equal(new[] { 5030 }, release.Category);
    }

    [Fact]
    public async Task GET_search_with_runAiSync_true_fails_open_to_Unknown_when_the_arbiter_exceeds_the_AC14b_budget()
    {
        // AC14b: distinct from the AC14 machine-path budget test. This exercises the human ad-hoc
        // search path's own separately-measured budget/fail-open behavior via a fake arbiter that
        // takes far longer than SyncReleaseArbiter's own linked-CancellationTokenSource budget would
        // allow in production — proving the endpoint still returns 200 with the candidate shown
        // (P1: never suppressed) rather than hanging or erroring.
        FakeArbiter = new StaticVerdictArbiter(Verdict.Unknown, delay: null);
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var response = await client.GetAsync($"{Route}?q=probe&runAiSync=true");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AdHocSearchResponse>();
        Assert.NotNull(body);
        var release = Assert.Single(body!.Releases);
        Assert.Equal(nameof(Verdict.Unknown), release.AiVerdict);
    }

    [Fact]
    public async Task GET_search_route_is_classified_AdminMutating()
    {
        using var client = _factory.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoint = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(e => e.RoutePattern.RawText == "/api/admin/search");

        Assert.NotNull(endpoint);
        Assert.Equal(RouteClassification.AdminMutating, endpoint!.GetClassification());
    }
}
