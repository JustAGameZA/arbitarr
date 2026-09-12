using Arbitarr.Core.Identity;
using Arbitarr.Data.Media;
using Arbitarr.Media.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-u1c: the identity resolver is actually WIRED, resolved from the real host's container rather
/// than constructed by a test.
/// </summary>
/// <remarks>
/// <para><b>WHY THIS FILE EXISTS.</b> The PR review found the shape this prevents: an earlier
/// revision took <c>AnimeListsProvider</c> as an optional constructor parameter, that provider was
/// registered nowhere, and DI therefore passed null — so the whole fallback tier was dead in
/// production while four unit tests passed by constructing it directly. A test that builds its
/// subject cannot see a missing registration; only one that asks the container can. Every dependency
/// this resolver needs is asserted here for that reason, not because resolution is interesting in
/// itself.</para>
///
/// <para>arb-5uw: the AnimeLists tier is now WIRED, and its registration is asserted below for
/// exactly that reason. It is a required constructor dependency, so a missing registration fails
/// here and at startup rather than leaving the tier dead — the shape this file was created to
/// prevent, now covering the tier that first exhibited it.</para>
///
/// <para>Registered is not the same as active. The tier's source URL
/// (<c>Arbitarr:AnimeLists:SourceUrl</c>) has no default, so on a stock host the provider resolves
/// and reports <see cref="AnimeListsProvider.IsConfigured"/> false — which these tests assert,
/// because "the registration exists AND the default install performs no third-party fetch" are two
/// separate claims and both are load-bearing.</para>
/// </remarks>
public sealed class IdentityResolverWiringTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public IdentityResolverWiringTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The contract the search endpoint asks for resolves, and resolves to the implementation the
    /// composition root intends. Asserting the concrete type matters: the endpoint takes
    /// <see cref="IIdentityResolver"/>, so a registration pointing at some other implementation
    /// would satisfy resolution while resolving nothing.
    /// </summary>
    [Fact]
    public void The_search_path_resolves_the_series_title_resolver_from_the_real_container()
    {
        using var scope = _factory.Services.CreateScope();

        var resolver = scope.ServiceProvider.GetService<IIdentityResolver>();

        Assert.NotNull(resolver);
        Assert.IsType<SeriesTitleResolver>(resolver);
    }

    /// <summary>
    /// Every constructor dependency resolves too. <see cref="SeriesTitleResolver"/>'s parameters are
    /// all REQUIRED now, so a missing registration fails here — and at startup — rather than
    /// silently disabling a tier the way an optional parameter defaulted to null did.
    /// </summary>
    [Fact]
    public void Every_dependency_the_resolver_needs_is_registered()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetService<SonarrCredentialProvider>());
        Assert.NotNull(services.GetService<IHttpClientFactory>());
        Assert.NotNull(services.GetService<IMemoryCache>());

        // arb-5uw: ADR 0002's fallback tier. This is the assertion the original shape needed and did
        // not have -- the provider was registered nowhere, an optional parameter swallowed the null,
        // and the tier was dead in production while its unit tests passed.
        Assert.NotNull(services.GetService<AnimeListsProvider>());
    }

    /// <summary>
    /// arb-5uw: a stock host has no <c>Arbitarr:AnimeLists:SourceUrl</c>, so the registered tier is
    /// INACTIVE — it will issue no request and fetch no third-party document until an operator
    /// configures one.
    /// </summary>
    /// <remarks>
    /// This is the property that let the tier be wired without the owner decision the bead was
    /// blocked on: no upstream is chosen anywhere in the repository, and no first-run network fetch
    /// can appear on the search path of an install that never opted in. Asserting the registration
    /// alone would not show it, since a registration carrying a committed default URL would satisfy
    /// the test above just as well.
    /// </remarks>
    [Fact]
    public void The_anime_lists_tier_is_registered_but_inactive_until_a_source_url_is_configured()
    {
        using var scope = _factory.Services.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<AnimeListsProvider>();

        Assert.False(provider.IsConfigured);
    }

    /// <summary>
    /// The named client the anime-lists fetch rides on exists and carries its timeout from the
    /// registration rather than from a per-call or constructor assignment.
    /// </summary>
    /// <remarks>
    /// The same fix, and the same guard, as the *arr client above: <see cref="AnimeListsProvider"/>
    /// is a SINGLETON holding this POOLED client for the life of the process, and
    /// <see cref="HttpClient.Timeout"/> throws once a request has started on an instance — so the
    /// constructor assignment this removed could not survive concurrent use. Asserting the
    /// registered value here is what keeps that assignment from being restored.
    /// </remarks>
    [Fact]
    public void The_named_anime_lists_client_carries_its_timeout_from_the_registration()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        using var client = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AnimeListsProvider.HttpClientName);

        Assert.Equal(
            services.GetRequiredService<AnimeListsProviderOptions>().EffectiveRequestTimeout,
            client.Timeout);
    }

    /// <summary>
    /// The named client the lookup rides on exists and carries its timeout from the registration
    /// rather than from a per-call assignment.
    /// </summary>
    /// <remarks>
    /// <c>ArrApiProvider</c> is constructed per call around this POOLED client, and
    /// <see cref="HttpClient.Timeout"/> throws once a request has started on an instance — so
    /// assigning it per call made concurrent searches throw. Asserting the registered value here is
    /// what keeps the fix from being undone by restoring that assignment: if the constructor set it
    /// again, this value would be whatever the last caller wanted rather than the configured
    /// default.
    /// </remarks>
    [Fact]
    public void The_named_arr_lookup_client_carries_its_timeout_from_the_registration()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        using var client = factory.CreateClient(SeriesTitleResolver.ArrHttpClientName);

        Assert.Equal(ArrApiProviderOptions.DefaultRequestTimeout, client.Timeout);
    }

    /// <summary>
    /// The memo has to OUTLIVE the request to memoise anything. The resolver is scoped (it reads
    /// per-request database state), so a scoped cache would be a fresh empty cache every request and
    /// the memo would silently do nothing — a performance fix that measures as no fix at all.
    /// </summary>
    [Fact]
    public void The_memo_is_shared_across_requests_rather_than_recreated_per_scope()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IMemoryCache>(),
            second.ServiceProvider.GetRequiredService<IMemoryCache>());
    }
}
