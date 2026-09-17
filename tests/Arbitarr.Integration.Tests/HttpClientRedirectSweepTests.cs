using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-673k: ADR 0014 (SEC-M1) requires <c>AllowAutoRedirect = false</c> on every typed/named
/// HttpClient registration in the composition root (<c>src/Arbitarr.Host/Program.cs</c>) — a
/// misconfigured upstream answering 3xx must never make this process silently re-issue a request
/// (often carrying an indexer or *arr API key) at a host nobody configured. Before this test, only
/// the Ollama registration was pinned (<c>ProgramOllamaHttpClientTests</c> in
/// <c>Arbitarr.Host.Tests</c>, itself a source-text guard: it reads Program.cs as a string and would
/// not notice a registration moved into a helper method, nor would it say anything about the other
/// thirteen registrations).
///
/// <para><b>This is a RUNTIME sweep against the REAL composed host</b>
/// (<see cref="ArbitarrWebApplicationFactory"/> boots <c>Program</c> unmodified), not a source-text
/// guard, so it survives a registration being refactored into a helper method or a new client being
/// added anywhere in the composition root.</para>
///
/// <para><b>How the client names are discovered — HONESTLY, not by hardcoding the roster.</b>
/// <c>IHttpClientFactory</c> has no public API to list every name it was ever asked to build. Two
/// internal-but-reachable sources between them cover every registration in this repository, because
/// every one of the 14 registrations here calls <c>.ConfigurePrimaryHttpMessageHandler(...)</c>:
/// <list type="bullet">
/// <item>Every typed client (<c>AddHttpClient&lt;TClient&gt;()</c>) is recorded by name in
/// <c>Microsoft.Extensions.DependencyInjection.HttpClientMappingRegistry.NamedClientRegistrations</c>
/// (internal type, reached by name via reflection, exactly like <c>DisableUriRedactionSwitchTests</c>
/// and <c>ConfiguredClientApiKeyResolverTests</c> already reach other internal/reflection-only
/// surfaces in this assembly).</item>
/// <item>Every named OR typed client that has ANY builder configuration (which in this codebase
/// means every one of them, since all fourteen set a primary handler) registers a
/// <c>Microsoft.Extensions.Options.ConfigureNamedOptions&lt;HttpClientFactoryOptions&gt;</c> whose
/// public <c>Name</c> property is exactly the client's registration name — resolvable without any
/// internal type, via <c>IServiceProvider.GetServices&lt;IConfigureOptions&lt;HttpClientFactoryOptions&gt;&gt;()</c>.</item>
/// </list>
/// The union of the two is <see cref="DiscoverClientNames"/>. A client registered with the bare,
/// zero-configuration <c>AddHttpClient(name)</c> overload and nothing else would be invisible to
/// both sources — but such a client would also default to <c>HttpClientHandler</c>'s own
/// <c>AllowAutoRedirect = true</c> with nothing overriding it, so it could never appear in this
/// repository without also being exactly the vulnerable case this sweep exists to catch; nothing
/// here is registered that way today (<see cref="DiscoveredNamesMatchTheKnownRoster"/> below pins the
/// full roster so a silent drop to that shape would be visible as a roster mismatch, not a false
/// green).</para>
///
/// <para><b>Non-vacuity.</b> <see cref="DiscoveredNamesMatchTheKnownRoster"/> asserts the sweep finds
/// at least every client named in Program.cs today, including the pre-existing Ollama pin
/// (<c>nameof(OllamaClient)</c>), so a change to the discovery mechanism that silently found nothing
/// would fail loudly rather than passing on an empty set.
/// <see cref="Sweep_flags_a_planted_client_that_allows_redirects_by_name"/> is the control: it
/// registers a THROWAWAY client with <c>AllowAutoRedirect = true</c> in a test-only service
/// collection built the same way the real host is, and shows the same assertion routine used against
/// the real host flags it BY NAME. Without this control, a sweep that discovered zero clients (a
/// silently broken reflection path, say) would report every registration as "vacuously fine".</para>
/// </summary>
public sealed class HttpClientRedirectSweepTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    /// <summary>
    /// Every client name Program.cs registers today, kept here ONLY so
    /// <see cref="DiscoveredNamesMatchTheKnownRoster"/> can assert the discovery mechanism actually
    /// found them all — never used to select which clients get swept. The sweep itself always drives
    /// off <see cref="DiscoverClientNames"/>.
    /// </summary>
    private static readonly string[] KnownRegisteredClientNames =
    [
        nameof(Arbitarr.Sources.NzbHydra.NzbHydraSource),
        Arbitarr.Host.Sources.SourceRegistry.NewznabHttpClientName,
        nameof(Arbitarr.Ai.OllamaClient),
        nameof(Arbitarr.Core.Sources.SourceConnectivityProber),
        nameof(Arbitarr.Core.Media.SonarrConnectivityProber),
        nameof(Arbitarr.Core.Media.RadarrConnectivityProber),
        nameof(Arbitarr.Core.Media.SonarrQueueClient),
        nameof(Arbitarr.Core.Media.RadarrQueueClient),
        nameof(Arbitarr.Core.Media.SonarrLibraryClient),
        nameof(Arbitarr.Core.Media.RadarrLibraryClient),
        Arbitarr.Media.Providers.SeriesTitleResolver.ArrHttpClientName,
        Arbitarr.Media.Providers.AnimeListsProvider.HttpClientName,
        nameof(Arbitarr.Core.Ai.OllamaConnectivityProber),
        nameof(Arbitarr.Core.Notifications.WebhookNotificationTransport),
    ];

    /// <summary>
    /// Registrations that are allowed to leave <c>AllowAutoRedirect</c> at its default (true).
    /// EMPTY ON PURPOSE. An entry would only be justified for a client that (a) never carries a
    /// credential in its request URI or headers, AND (b) only ever talks to a target Arbitarr itself
    /// pins by exact host/port at the point of use, so a redirect could not retarget it to an
    /// unpinned origin. No client in this codebase meets both bars today — every one either carries
    /// an *arr/indexer API key or reads an operator-supplied address (ADR 0014).
    /// </summary>
    private static readonly HashSet<string> AllowedToPermitRedirects = new(StringComparer.Ordinal);

    private readonly ArbitarrWebApplicationFactory _factory;

    public HttpClientRedirectSweepTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Every_registered_HttpClient_refuses_automatic_redirects()
    {
        // CreateClient() forces host startup so Services reflects the fully composed container,
        // matching every other test in this assembly that reads Program.cs's real registrations.
        using var warmupClient = _factory.CreateClient();

        var provider = _factory.Services;
        var names = DiscoverClientNames(provider);

        Assert.NotEmpty(names);

        var offenders = new List<string>();

        foreach (var name in names)
        {
            if (AllowedToPermitRedirects.Contains(name))
            {
                continue;
            }

            if (!ClientRefusesAutoRedirect(provider, name, out var reason))
            {
                offenders.Add($"{name}: {reason}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The following HttpClient registration(s) allow automatic redirects, violating ADR 0014 "
            + $"(SEC-M1):{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    /// <summary>
    /// Pins that <see cref="DiscoverClientNames"/> is not silently discovering an empty or partial
    /// set — the failure mode a passing sweep over zero clients would otherwise hide entirely.
    /// </summary>
    [Fact]
    public void DiscoveredNamesMatchTheKnownRoster()
    {
        using var warmupClient = _factory.CreateClient();

        var discovered = DiscoverClientNames(_factory.Services);

        foreach (var expected in KnownRegisteredClientNames)
        {
            Assert.Contains(expected, discovered);
        }
    }

    /// <summary>
    /// The non-vacuity control: proves the assertion routine this test relies on WOULD fail, by
    /// running it against a throwaway service collection carrying one client with redirects
    /// deliberately left on. Built independently of <see cref="ArbitarrWebApplicationFactory"/> (a
    /// plain <see cref="ServiceCollection"/> with <c>AddHttpClient</c>) rather than by mutating
    /// anything in the real host, per CLAUDE.md section 4's rule against planting vulnerable code in
    /// the repository proper — this is a same-shape, throwaway model of the mechanism, not the real
    /// host with a defect introduced.
    /// </summary>
    [Fact]
    public void Sweep_flags_a_planted_client_that_allows_redirects_by_name()
    {
        const string vulnerableClientName = "arb-673k-control-client-with-redirects-enabled";

        var services = new ServiceCollection();
        services.AddHttpClient(vulnerableClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = true });
        services.AddHttpClient("SafeSibling")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        using var provider = services.BuildServiceProvider();

        var names = DiscoverClientNames(provider);
        Assert.Contains(vulnerableClientName, names);
        Assert.Contains("SafeSibling", names);

        var offenders = names
            .Where(name => !ClientRefusesAutoRedirect(provider, name, out _))
            .ToArray();

        Assert.Equal([vulnerableClientName], offenders);
    }

    /// <summary>
    /// Every distinct HttpClient registration name reachable from <paramref name="provider"/>. See
    /// the type doc for why this union of two sources is the honest, no-hardcoded-roster mechanism
    /// rather than a list this test maintains by hand.
    /// </summary>
    private static IReadOnlyCollection<string> DiscoverClientNames(IServiceProvider provider)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        var registryType = typeof(IHttpClientFactory).Assembly
            .GetType("Microsoft.Extensions.DependencyInjection.HttpClientMappingRegistry");
        if (registryType is not null)
        {
            var registryInstance = provider.GetService(registryType);
            var namedClientRegistrations = registryType
                .GetProperty("NamedClientRegistrations")?
                .GetValue(registryInstance) as System.Collections.IDictionary;

            if (namedClientRegistrations is not null)
            {
                foreach (System.Collections.DictionaryEntry entry in namedClientRegistrations)
                {
                    if (entry.Key is string typedClientName)
                    {
                        names.Add(typedClientName);
                    }
                }
            }
        }

        foreach (var configureOptions in provider.GetServices<IConfigureOptions<HttpClientFactoryOptions>>())
        {
            var name = configureOptions.GetType().GetProperty("Name")?.GetValue(configureOptions) as string;
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Builds the named client's primary handler exactly as <see cref="IHttpClientFactory"/> would
    /// (via <see cref="HttpMessageHandlerBuilder"/> and the registered
    /// <c>ConfigurePrimaryHttpMessageHandler</c> action pipeline reachable through
    /// <see cref="IOptionsMonitor{TOptions}"/>), unwraps any <see cref="DelegatingHandler"/> chain to
    /// the innermost handler, and asserts it is an <see cref="HttpClientHandler"/> or
    /// <see cref="SocketsHttpHandler"/> with <c>AllowAutoRedirect = false</c>.
    /// </summary>
    private static bool ClientRefusesAutoRedirect(IServiceProvider provider, string name, out string reason)
    {
        using var scope = provider.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<HttpMessageHandlerBuilder>();
        builder.Name = name;

        var optionsMonitor = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
        var options = optionsMonitor.Get(name);

        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        var handler = builder.PrimaryHandler;
        var innermost = UnwrapToInnermostHandler(handler);

        switch (innermost)
        {
            case HttpClientHandler httpClientHandler:
                if (httpClientHandler.AllowAutoRedirect)
                {
                    reason = "HttpClientHandler.AllowAutoRedirect is true (expected false)";
                    return false;
                }

                reason = "";
                return true;

            case SocketsHttpHandler socketsHttpHandler:
                if (socketsHttpHandler.AllowAutoRedirect)
                {
                    reason = "SocketsHttpHandler.AllowAutoRedirect is true (expected false)";
                    return false;
                }

                reason = "";
                return true;

            default:
                reason = $"primary handler is {innermost?.GetType().FullName ?? "null"}, "
                    + "neither HttpClientHandler nor SocketsHttpHandler — cannot verify AllowAutoRedirect";
                return false;
        }
    }

    private static HttpMessageHandler? UnwrapToInnermostHandler(HttpMessageHandler? handler)
    {
        while (handler is DelegatingHandler delegatingHandler)
        {
            handler = delegatingHandler.InnerHandler;
        }

        return handler;
    }
}
