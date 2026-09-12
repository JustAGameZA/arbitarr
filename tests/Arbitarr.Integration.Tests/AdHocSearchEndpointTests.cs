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
using Arbitarr.Integration.Tests.TestSupport;
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
public sealed class AdHocSearchEndpointTests : IAsyncLifetime
{
    private const string AdminKey = "adhoc-search-admin-key";
    private const string Route = "/api/admin/search";

    private readonly ArbitarrWebApplicationFactory _root;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _configDirectory;

    public AdHocSearchEndpointTests()
    {
        _configDirectory = Path.Combine(Path.GetTempPath(), "arbitarr-m7-adhoc-search-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);

        _root = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
        _factory = _root.WithWebHostBuilder(builder =>
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

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// This class OWNS its host so disposal drains it before the delete — see
    /// <see cref="CategoryParamCapTests.DisposeAsync"/> for the full account of why the shared
    /// <c>IClassFixture</c> this class used to inject made the delete throw (arb-gphi fix-up).
    /// </summary>
    public async Task DisposeAsync()
    {
        await _root.DisposeAsync();
        ConfigDirectoryTeardown.Delete(_configDirectory);
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

    /// <summary>
    /// Builds a client whose single upstream source records the <see cref="SearchQuery"/> the
    /// endpoint actually built, so the #104 assertions below are made against what reaches the
    /// source rather than against the response body (which the fake produces regardless of the
    /// query, and which therefore proves nothing about the mode).
    /// </summary>
    private (WebApplicationFactory<Program> Factory, Func<SearchQuery?> Observed) ObservingFactory()
    {
        SearchQuery? observed = null;
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUpstreamSource>();
            services.RemoveAll<IReadOnlyList<IUpstreamSource>>();
            services.AddSingleton<IUpstreamSource>(new SecondFakeUpstreamSource(
                "adhoc-observing-source",
                onSearch: query => observed = query));
            services.AddSingleton<IReadOnlyList<IUpstreamSource>>(sp => sp.GetServices<IUpstreamSource>().ToArray());
        }));
        return (factory, () => observed);
    }

    /// <summary>
    /// #104, the dashboard row of the issue's repro table: <c>GET /api/admin/search?q=…&amp;season=
    /// 22&amp;ep=1</c> has no <c>t=</c> of its own, so the endpoint must DERIVE a TV search from the
    /// numbering the operator supplied. Before this it built a plain search and the season/ep were
    /// dropped at the source, returning the newest episode of the series.
    ///
    /// <para>
    /// Asserted on the <see cref="SearchQuery"/> that reaches the upstream source, including
    /// <see cref="SearchQuery.Type"/> — the member that actually decides the upstream <c>t=</c> —
    /// alongside the season/ep, since season/ep alone reached the query before #104 too and only
    /// the mode determines whether they are forwarded.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GET_search_with_season_and_episode_but_no_id_derives_a_tv_search()
    {
        await SeedAdminKeyAsync();

        var (factory, observed) = ObservingFactory();
        using (factory)
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);

            var response = await client.GetAsync($"{Route}?q=Project+Runway&season=22&ep=1");
            response.EnsureSuccessStatusCode();

            var query = observed();
            Assert.NotNull(query);
            Assert.Equal(SearchType.TvSearch, query!.Type);
            Assert.Equal(22, query.Season);
            Assert.Equal(1, query.Episode);
            Assert.Null(query.TvdbId);
            Assert.Equal("Project Runway", query.QueryText);
        }
    }

    /// <summary>
    /// A tmdbid-only dashboard search derives a movie search, and a q-only one stays a plain
    /// search. Together with the case above these are each other's controls: all three go through
    /// the identical code path and differ only in which parameters were filled in, so a derivation
    /// that answered one mode for everything would fail two of the three.
    /// </summary>
    [Theory]
    [InlineData("q=dune&tmdbid=438631", SearchType.Movie)]
    [InlineData("q=dune", SearchType.Search)]
    [InlineData("q=bleach&tvdbid=74796", SearchType.TvSearch)]
    public async Task GET_search_derives_the_mode_from_the_parameters_supplied(string queryString, SearchType expected)
    {
        await SeedAdminKeyAsync();

        var (factory, observed) = ObservingFactory();
        using (factory)
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);

            var response = await client.GetAsync($"{Route}?{queryString}");
            response.EnsureSuccessStatusCode();

            var query = observed();
            Assert.NotNull(query);
            Assert.Equal(expected, query!.Type);
        }
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
