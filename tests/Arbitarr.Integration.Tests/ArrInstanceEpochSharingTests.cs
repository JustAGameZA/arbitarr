using Arbitarr.Core.Media;
using Arbitarr.Data.Media;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-biyh (from the arb-iiy / #226 architectural review): the only thing pinning
/// <see cref="ArrInstanceRepository"/> and <see cref="Arbitarr.Media.Providers.SeriesTitleResolver"/> to the
/// SAME <see cref="IArrInstanceEpoch"/> is the <c>AddSingleton</c> line in <c>Program.cs</c> — the
/// repository's constructor parameter is optional (defaulting to a private instance) while the resolver's is
/// required, so a test that constructs either directly cannot see a registration mistake that left them on
/// two independent counters. This test asks the real built container for both, the way
/// <see cref="IdentityResolverWiringTests"/> and <see cref="ClassifierPollingWorkerCompositionTests"/> do.
/// </summary>
public sealed class ArrInstanceEpochSharingTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string BaseUrl = "http://192.0.2.33:8989/";
    private const string ApiKey = "placeholder-sonarr-key-9f31ac02";

    private readonly ArbitarrWebApplicationFactory _factory;

    public ArrInstanceEpochSharingTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Resolves the epoch the resolver would read and the repository from the SAME container scope,
    /// bumps it via an accepted <see cref="ArrInstanceRepository.SetAsync"/> write, and asserts the shared
    /// epoch observed the bump.
    /// </summary>
    /// <remarks>
    /// The positive control is <c>before != after</c>: two independent <see cref="IArrInstanceEpoch"/>
    /// instances that both happen to start at 0 would otherwise let this test pass while proving nothing
    /// about sharing. Asserting a real change on the SAME resolved instance is what makes "the resolver
    /// observes the bump" load-bearing rather than a starting-value coincidence.
    /// </remarks>
    [Fact]
    public async Task A_write_through_the_repository_bumps_the_epoch_the_resolver_would_also_see()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        // IArrInstanceEpoch is registered AddSingleton, so resolving it here returns the exact instance
        // SeriesTitleResolver's constructor also receives — that identity is the whole property under test.
        var epoch = services.GetRequiredService<IArrInstanceEpoch>();
        var repository = services.GetRequiredService<ArrInstanceRepository>();

        var before = epoch.Current;

        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var after = epoch.Current;

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// <see cref="ArrInstanceRepository"/> is registered scoped (see <c>Program.cs</c>), so a second scope
    /// must resolve a DIFFERENT repository instance while still sharing the SAME singleton epoch — that
    /// combination is exactly the shape the resolver, itself scoped, depends on.
    /// </summary>
    [Fact]
    public void The_epoch_is_the_same_singleton_across_scopes_while_the_repository_is_not()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        var firstEpoch = first.ServiceProvider.GetRequiredService<IArrInstanceEpoch>();
        var secondEpoch = second.ServiceProvider.GetRequiredService<IArrInstanceEpoch>();

        var firstRepository = first.ServiceProvider.GetRequiredService<ArrInstanceRepository>();
        var secondRepository = second.ServiceProvider.GetRequiredService<ArrInstanceRepository>();

        Assert.Same(firstEpoch, secondEpoch);
        Assert.NotSame(firstRepository, secondRepository);
    }
}
