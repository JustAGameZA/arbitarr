using Arbitarr.Core.Sources;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-x7w8.4: the source registry is actually WIRED — <c>IReadOnlyList&lt;IUpstreamSource&gt;</c>
/// resolves from the REAL composition root, over the real <c>Sources</c> rows, with each source
/// carrying its own redirect-disabled client and its own timeout.
/// </summary>
/// <remarks>
/// <para><b>WHY A CONTAINER TEST AND NOT ONLY UNIT TESTS.</b> <c>SourceRegistryTests</c> builds its
/// own subject, and a subject a test constructs cannot see a missing registration — the shape
/// arb-u1c's review caught, where four unit tests passed while DI left the dependency null. A
/// registry that resolved perfectly in isolation and was never registered would leave the search
/// path with no sources at all while every unit test stayed green.</para>
///
/// <para>This class OWNS its factory rather than injecting the shared class fixture, because it
/// seeds source rows: the shared fixture is used by ~29 other classes whose assertions would then be
/// running against a source set this file had written into.</para>
/// </remarks>
public sealed class SourceRegistryWiringTests : IAsyncLifetime
{
    // The value the pre-commit secret guard allowlists, with a suffix that keeps it distinctive when
    // searching log rows.
    private const string PlantedKey = "secret-api-key-registry-resolution-probe";

    // RFC 5737 TEST-NET-1: non-routable, and no real address is committed.
    private const string NewznabBaseUrl = "http://192.0.2.90:9117";
    private const string HydraBaseUrl = "http://192.0.2.91:5076";
    private const string TorznabBaseUrl = "http://192.0.2.92:9117";

    private readonly ArbitarrWebApplicationFactory _factory;
    private readonly string _configDirectory;

    public SourceRegistryWiringTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-registry-wiring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);
        _factory = ArbitarrWebApplicationFactory.OverConfigDirectory(_configDirectory);
    }

    /// <summary>
    /// Seeds two enabled sources of different kinds, the first carrying a real stored API key —
    /// which is what makes the log assertion below a test of a key that was genuinely in play.
    /// </summary>
    public async Task InitializeAsync() =>
        await _factory.SeedAsync(async db =>
        {
            var keyed = new Source
            {
                Kind = SourceRepository.NewznabKind,
                DisplayName = "registry-wiring-newznab",
                BaseUrl = NewznabBaseUrl,
                ApiPath = "/api",
                Priority = 10,
                TimeoutSeconds = 17,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Sources.Add(keyed);
            await db.SaveChangesAsync();

            db.Settings.Add(new SettingEntry
            {
                Name = SourceRepository.ApiKeySettingName(keyed.Id),
                Value = PlantedKey,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            db.Sources.Add(new Source
            {
                Kind = SourceRepository.NzbHydraKind,
                DisplayName = "registry-wiring-hydra",
                BaseUrl = HydraBaseUrl,
                ApiPath = "/api",
                Priority = 5,
                TimeoutSeconds = 23,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

            // A THIRD source, with a third distinct timeout. Two would let a single shared client
            // produce two equal values that a weaker assertion could accept; three distinguishes a
            // correct per-source resolution from the shared-client mutant unambiguously.
            db.Sources.Add(new Source
            {
                Kind = SourceRepository.TorznabKind,
                DisplayName = "registry-wiring-torznab",
                BaseUrl = TorznabBaseUrl,
                ApiPath = "/api",
                Priority = 1,
                TimeoutSeconds = 31,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        });

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        ConfigDirectoryTeardown.Delete(_configDirectory);
    }

    /// <summary>
    /// The set the search path asks for resolves from the real container, and carries EVERY seeded
    /// source in priority order. The composition root registers no single
    /// <see cref="IUpstreamSource"/> and no fixed list any more, so awaiting
    /// <see cref="ISourceRegistry.ResolveAsync"/> is the only way to get them.
    /// </summary>
    [Fact]
    public async Task The_search_path_resolves_every_enabled_source_from_the_real_container()
    {
        using var scope = _factory.Services.CreateScope();

        var registry = scope.ServiceProvider.GetService<ISourceRegistry>();

        Assert.NotNull(registry);
        var sources = await registry.ResolveAsync(CancellationToken.None);
        Assert.Equal(
            new[] { "registry-wiring-newznab", "registry-wiring-hydra", "registry-wiring-torznab" },
            sources.Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// The registry itself, and the credential provider it reads keys through, are both registered.
    /// A missing provider registration would fail at the first search rather than at startup.
    /// </summary>
    [Fact]
    public void Every_dependency_the_registry_needs_is_registered()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<SourceRegistry>());
        Assert.NotNull(scope.ServiceProvider.GetService<SourceCredentialProvider>());
    }

    /// <summary>
    /// <b>EVERY resolved source's client disables automatic redirects (SEC-M1), asserted PER SOURCE
    /// across the whole set — not once on a named registration.</b>
    /// </summary>
    /// <remarks>
    /// <para>An upstream answering 30x would otherwise make this process reissue the request —
    /// carrying that source's API key — at a host nobody configured, before the adapter's own origin
    /// check ever saw the real target.</para>
    ///
    /// <para><b>Per source, because that is where the property can be lost.</b> Nothing pinned this
    /// for the Newznab adapter before arb-x7w8.4: a second named registration added without
    /// <c>ConfigurePrimaryHttpMessageHandler</c> would leave every direct indexer following
    /// redirects while the Hydra client stayed correct. Walking the RESOLVED sources rather than
    /// asking the factory for a name also means a registry that quietly built a source over some
    /// other client fails here, which asking by name cannot see.</para>
    /// </remarks>
    [Fact]
    public async Task Every_resolved_source_refuses_to_follow_redirects()
    {
        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISourceRegistry>();
        var sources = await registry.ResolveAsync(CancellationToken.None);

        // The control: there is more than one source and both kinds are represented, so "every
        // source is safe" is a statement about a populated, mixed set rather than a trivial one.
        Assert.Equal(3, sources.Count);

        Assert.All(sources, source => Assert.False(RedirectsAreFollowedBy(ClientOf(source))));
    }

    /// <summary>
    /// Each resolved source carries ITS OWN row's timeout, asserted across an N=3 set with three
    /// DIFFERENT values. This is the seam NewznabSource:46-51 states: the adapters assign
    /// <see cref="HttpClient.Timeout"/> on the client they are handed, so a single client shared
    /// across sources would make the last-constructed row's timeout win for all of them. Three
    /// distinct values is what tells a correct resolution from that mutant — with two, a shared
    /// client still produces two equal values that a weaker assertion could accept.
    /// </summary>
    [Fact]
    public async Task Each_resolved_source_carries_its_own_rows_timeout()
    {
        using var scope = _factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISourceRegistry>();

        var sources = await registry.ResolveAsync(CancellationToken.None);

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(31) },
            sources.Select(s => ClientOf(s).Timeout).ToArray());

        // Reference equality, because three clients that were ONE instance would still be three
        // entries here while carrying a single timeout.
        Assert.Equal(3, sources.Select(ClientOf).Distinct().Count());
    }

    /// <summary>
    /// <b>A stored source key never reaches the log store along the DI-RESOLUTION path, and the
    /// POSITIVE CONTROL is what makes that assertion bite.</b>
    /// </summary>
    /// <remarks>
    /// <para>"The key appears in no log row" passes just as happily when the key was never in play —
    /// an empty set contains nothing, which is the vacuous shape CLAUDE.md §4 forbids and the one
    /// that let #57, #78 and #80 ship real leaks. So this proves the sink WOULD have shown it first:
    /// a line carrying the same planted key is deliberately logged through a name the cleanser's
    /// query-string rule does not cover, read back, and asserted to be present in the store. Only
    /// once the sink is demonstrably capable of surfacing this exact value does the absence
    /// assertion over the resolution path's own rows mean anything.</para>
    ///
    /// <para><b>TWO controls, because the two leak shapes fail differently.</b> The first logs the
    /// key as a BARE value, which nothing scrubs — that is what proves the sink can surface this
    /// exact string, so an absence over the resolution path's rows is a real absence. The second is
    /// <see cref="LogSecretInjectionTests"/>'s shape: the key inside a URL query string, asserted to
    /// come back carrying <see cref="LogMessageCleanser.Replacement"/>, which proves the cleanser is
    /// wired into this sink's write path rather than merely correct in isolation. A leak from the
    /// registry could take either form — a bare interpolation, or a request URI — and only having
    /// both controls makes the absence assertion cover both.</para>
    /// </remarks>
    [Fact]
    public async Task Resolving_the_sources_puts_no_key_in_the_logs()
    {
        var store = _factory.Services.GetRequiredService<LogStore>();
        var loggerFactory = _factory.Services.GetRequiredService<ILoggerFactory>();

        // CONTROL 1. A bare value, not in a query string, so nothing scrubs it: if this does not come
        // back from the store, the search below is looking at a sink that cannot show this value and
        // every absence assertion in this test is vacuous.
        loggerFactory
            .CreateLogger("Arbitarr.Test.RegistryLeakControl")
            .LogWarning("control line carrying the planted value {Planted}", PlantedKey);

        // CONTROL 2. The same key inside a request URI — the shape a leak from an HTTP path would
        // actually take. It must come back SCRUBBED, which proves the cleanser runs on this sink's
        // write path (a correct cleanser that nothing calls is the failure a unit test cannot see).
        loggerFactory
            .CreateLogger("Arbitarr.Test.RegistryCleanserControl")
            .LogWarning("upstream request failed: http://192.0.2.93:9117/api?t=search&apikey={0}", PlantedKey);

        // The path under test: resolve the sources exactly as a request does.
        using (var scope = _factory.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<ISourceRegistry>();
            var sources = await registry.ResolveAsync(CancellationToken.None);
            Assert.Equal(3, sources.Count);
        }

        await FlushLogSinkAsync();

        var page = await store.ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        var bareControl = page.Entries
            .Where(e => e.Logger.Contains("RegistryLeakControl", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(bareControl);
        Assert.Contains(bareControl, e => e.Message.Contains(PlantedKey, StringComparison.Ordinal));

        var cleanserControl = page.Entries
            .Where(e => e.Logger.Contains("RegistryCleanserControl", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(cleanserControl);
        foreach (var entry in cleanserControl)
        {
            Assert.DoesNotContain(PlantedKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal);
        }

        // Now the real assertion, over every row the resolution path could have written. Every field
        // is checked, not just the message: an exception's text is the one most likely to carry a
        // request URI, and checking Message alone would let the most probable leak through while
        // looking thorough.
        foreach (var entry in page.Entries.Except(bareControl).Except(cleanserControl))
        {
            Assert.DoesNotContain(PlantedKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PlantedKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PlantedKey, entry.Logger, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <see cref="HttpClient"/> exposes no redirect property, so this reads it off the handler chain
    /// the factory built — which is the value that actually governs the request.
    /// </summary>
    private static bool RedirectsAreFollowedBy(HttpClient client)
    {
        var handlerField = typeof(HttpMessageInvoker)
            .GetField("_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = handlerField?.GetValue(client);

        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        return handler is HttpClientHandler { AllowAutoRedirect: true };
    }

    /// <summary>
    /// The <see cref="HttpClient"/> an adapter owns. The adapters hold it privately — deliberately,
    /// so nothing outside can retune it mid-flight — so this reads it back the same way, and both the
    /// redirect and the timeout assertions above go through the client the source will actually use
    /// rather than through a registration looked up by name.
    /// </summary>
    private static HttpClient ClientOf(IUpstreamSource source)
    {
        var field = source.GetType()
            .GetField("_httpClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        return field?.GetValue(source) as HttpClient
            ?? throw new InvalidOperationException(
                $"{source.GetType().Name} no longer holds its HttpClient in _httpClient; this test "
                + "reads the client the adapter was given and must be updated with the field.");
    }

    /// <summary>
    /// Waits for the sink's background pump to drain. The provider batches on an interval by design
    /// (it must never write on the caller's thread), so a read taken immediately after would see
    /// nothing yet — and the positive control above would fail for a reason that is not a leak.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));
}
