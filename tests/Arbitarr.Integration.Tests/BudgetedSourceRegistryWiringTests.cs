using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-x7w8.10: the budget gate is actually WIRED — <see cref="ISourceRegistry"/> resolved from the
/// REAL composition root hands back <see cref="BudgetedUpstreamSource"/> instances, one per enabled
/// row, and a source at its limit records a skip while the other is queried.
/// </summary>
/// <remarks>
/// <para><b>WHY THIS CLASS EXISTS AT ALL.</b> An earlier revision of this bead attached the gate to
/// Program.cs's <c>IReadOnlyList&lt;IUpstreamSource&gt;</c> registration. arb-x7w8.4 removed that
/// registration, and the failure mode was SILENT: the factory stayed registered, nothing called it,
/// every unit test of the decorator still passed, and budgets simply stopped being enforced. No test
/// that builds its own subject can see that — only one that asks the real container for the
/// interface the search path asks for. This is that test.</para>
///
/// <para><b>The positive control is the other half.</b> "Every resolved source is a
/// <see cref="BudgetedUpstreamSource"/>" passes vacuously against a set that was never populated,
/// which is the shape CLAUDE.md §4 forbids. So the same assertion is run against the undecorated
/// <see cref="SourceRegistry"/> resolved from the SAME container over the SAME rows, and asserted to
/// come out the other way — proving the assertion is capable of detecting an undecorated composition
/// root rather than merely passing.</para>
///
/// <para>This class OWNS its factory rather than taking the shared fixture, because it seeds source
/// rows and writes hit events: a shared fixture would leave both visible to the other classes
/// using it.</para>
/// </remarks>
public sealed class BudgetedSourceRegistryWiringTests : IAsyncLifetime
{
    // RFC 5737 TEST-NET-1: non-routable, and no real address is committed.
    private const string LimitedBaseUrl = "http://192.0.2.110:9117";
    private const string UnlimitedBaseUrl = "http://192.0.2.111:9117";

    private const string LimitedName = "budget-wiring-limited";
    private const string UnlimitedName = "budget-wiring-unlimited";

    private readonly ArbitarrWebApplicationFactory _factory;
    private readonly string _configDirectory;

    public BudgetedSourceRegistryWiringTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-budget-wiring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);
        _factory = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
    }

    /// <summary>
    /// TWO enabled rows. The first carries <c>QueryLimit = 0</c> — the state an operator sets to mean
    /// "do not query this indexer", and the one that is distinct from a null limit — and the second
    /// carries no limit at all. Two rows rather than one is what lets the skip assertion say ONLY the
    /// limited source skipped, which a decorator that gated everything would fail.
    /// </summary>
    public async Task InitializeAsync() =>
        await _factory.SeedAsync(async db =>
        {
            db.Sources.Add(new Source
            {
                Kind = SourceRepository.NewznabKind,
                DisplayName = LimitedName,
                BaseUrl = LimitedBaseUrl,
                ApiPath = "/api",
                Priority = 10,
                QueryLimit = 0,
                LimitsUnit = "Day",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            db.Sources.Add(new Source
            {
                Kind = SourceRepository.NewznabKind,
                DisplayName = UnlimitedName,
                BaseUrl = UnlimitedBaseUrl,
                ApiPath = "/api",
                Priority = 5,
                QueryLimit = null,
                LimitsUnit = "Day",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        });

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        ConfigDirectoryTeardown.Delete(_configDirectory);
    }

    /// <summary>
    /// EVERY source the real container resolves through <see cref="ISourceRegistry"/> is gated —
    /// asserted PER SOURCE across N=2, not "at least one is". A decorator that wrapped only the row
    /// it happened to match would satisfy a single-source assertion and leave the second indexer
    /// ungated, which is exactly the shape this bead's whole risk is.
    /// </summary>
    [Fact]
    public async Task Every_source_the_search_path_resolves_is_gated()
    {
        using var scope = _factory.Services.CreateScope();

        var sources = await scope.ServiceProvider
            .GetRequiredService<ISourceRegistry>()
            .ResolveAsync(CancellationToken.None);

        Assert.Equal(2, sources.Count);
        Assert.All(sources, source => Assert.IsType<BudgetedUpstreamSource>(source));
    }

    /// <summary>
    /// THE POSITIVE CONTROL for the assertion above. The same assertion against the UNDECORATED
    /// <see cref="SourceRegistry"/>, resolved from the same container over the same rows, comes out
    /// the OTHER WAY — which is what proves it would detect a composition root that stopped
    /// decorating. Without this, "every resolved source is a BudgetedUpstreamSource" is an assertion
    /// nobody has shown can be false.
    /// </summary>
    [Fact]
    public async Task The_undecorated_registry_yields_ungated_sources()
    {
        using var scope = _factory.Services.CreateScope();

        var sources = await scope.ServiceProvider
            .GetRequiredService<SourceRegistry>()
            .ResolveAsync(CancellationToken.None);

        // Same population as the gated case, so the type assertion below is about decoration and not
        // about an empty set.
        Assert.Equal(2, sources.Count);
        Assert.All(sources, source => Assert.IsNotType<BudgetedUpstreamSource>(source));
    }

    /// <summary>
    /// The gate BITES on the real wiring: a search through the source whose row says
    /// <c>QueryLimit = 0</c> records a <see cref="EventKind.SourceSkipped"/> naming THAT source and
    /// makes no call, while the unlimited source is let through to its own client.
    ///
    /// <para>Asserted PER SOURCE — the skip event must name the limited source and must NOT name the
    /// unlimited one. "A skip was recorded" would pass against a decorator that gated everything,
    /// which is the same defect as gating nothing, seen from the other side. It also pins that the
    /// pairing put each row's OWN limits on its own source: a lookup that put one row's limits on
    /// both would skip both or neither.</para>
    /// </summary>
    [Fact]
    public async Task Only_the_source_at_its_limit_records_a_skip()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var sources = await scope.ServiceProvider
                .GetRequiredService<ISourceRegistry>()
                .ResolveAsync(CancellationToken.None);

            // Highest priority first, so index 0 is the QueryLimit = 0 row.
            Assert.Equal(LimitedName, sources[0].Name);
            Assert.Equal(UnlimitedName, sources[1].Name);

            // The limited source is refused by the gate and answers empty without calling anything.
            Assert.Empty(await sources[0].SearchAsync(Query()));

            // The unlimited source is let THROUGH the gate and reaches its own HttpClient, which
            // cannot connect to a TEST-NET address. The adapter turns that into an empty answer
            // rather than a throw, so the assertion that bites is the event one below: only the
            // limited source produced a skip.
            await SearchIgnoringUpstreamFailureAsync(sources[1]);
        }

        using var readScope = _factory.Services.CreateScope();
        var db = readScope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();

        var skips = await db.Events
            .AsNoTracking()
            .Where(e => e.Kind == EventKind.SourceSkipped)
            .ToListAsync();

        Assert.Contains(skips, e => e.SourceDisplayName == LimitedName);
        Assert.DoesNotContain(skips, e => e.SourceDisplayName == UnlimitedName);
    }

    /// <summary>
    /// Searches a source that is expected to be let through the gate and then fail to reach its
    /// TEST-NET address. The upstream failure is not what is under test here — that the call was
    /// ATTEMPTED is — so it is swallowed rather than asserted on; the assertion is the absence of a
    /// skip event for this source.
    /// </summary>
    private static async Task SearchIgnoringUpstreamFailureAsync(IUpstreamSource source)
    {
        try
        {
            await source.SearchAsync(Query());
        }
        catch (Exception)
        {
            // Expected: nothing answers on a non-routable address.
        }
    }

    private static SearchQuery Query() =>
        new("wiring-probe", Array.Empty<int>(), Limit: 10, SearchProtocol.Newznab);
}
