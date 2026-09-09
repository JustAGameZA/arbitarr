using Arbitarr.Api.Search;
using Arbitarr.Host.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-b5z: the source-set fingerprint is actually WIRED — resolved from the real host container
/// rather than constructed by a test.
/// </summary>
/// <remarks>
/// <para><b>WHY THIS FILE EXISTS.</b> The mechanism is inert unless the composition root supplies a
/// non-empty fingerprint: <see cref="PaginationSnapshotService"/> defaults to
/// <see cref="StaticSourceSetFingerprintSource.Empty"/>, which reproduces the pre-arb-b5z token
/// exactly. So a version of this change that added the token component, passed every unit test, and
/// registered nothing would fix nothing in production while looking complete — the same shape as the
/// unregistered provider caught in arb-u1c's review, where four unit tests passed by constructing
/// the subject directly while DI left it null. A test that builds its own subject cannot see a
/// missing registration; only one that asks the container can.</para>
/// </remarks>
public sealed class SourceSetFingerprintWiringTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public SourceSetFingerprintWiringTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The contract resolves, and resolves to the implementation that reads the resolved source
    /// configuration. Asserting the concrete type matters: a registration pointing at
    /// <see cref="StaticSourceSetFingerprintSource"/> would satisfy resolution while leaving the
    /// token exactly as it was.
    /// </summary>
    [Fact]
    public void The_host_registers_the_resolved_source_set_fingerprint()
    {
        using var scope = _factory.Services.CreateScope();

        var source = scope.ServiceProvider.GetService<ISourceSetFingerprintSource>();

        Assert.NotNull(source);
        Assert.IsType<ResolvedSourceSetFingerprintSource>(source);
    }

    /// <summary>
    /// The registered source yields a non-empty fingerprint, which is what makes the token component
    /// do anything at all. The empty string is the documented "no fingerprint" value, so producing it
    /// here would mean the wiring is present but inert.
    /// </summary>
    [Fact]
    public async Task The_registered_fingerprint_is_not_the_inert_empty_value()
    {
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<ISourceSetFingerprintSource>();

        var fingerprint = await source.GetAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(fingerprint));
        Assert.NotEqual(
            await StaticSourceSetFingerprintSource.Empty.GetAsync(CancellationToken.None),
            fingerprint);
    }

    /// <summary>
    /// The fingerprint is stable across requests within one process. It is derived from
    /// configuration resolved once at startup, and a value that varied per request would give every
    /// request its own snapshot token — silently disabling the pagination snapshot entirely, which is
    /// a far worse regression than the staleness this change fixes.
    /// </summary>
    [Fact]
    public async Task The_fingerprint_is_stable_across_scopes()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();

        var firstFingerprint = await first.ServiceProvider
            .GetRequiredService<ISourceSetFingerprintSource>()
            .GetAsync(CancellationToken.None);
        var secondFingerprint = await second.ServiceProvider
            .GetRequiredService<ISourceSetFingerprintSource>()
            .GetAsync(CancellationToken.None);

        Assert.Equal(firstFingerprint, secondFingerprint);
    }

    /// <summary>
    /// The API key never reaches the fingerprint. It is not part of "which sources produced this
    /// result set", and the fingerprint is hashed, cached, logged about and compared in tests — not a
    /// surface that should carry a secret.
    /// </summary>
    /// <remarks>
    /// The POSITIVE CONTROL is the second assertion: a fingerprint built from a
    /// <see cref="ResolvedSourceConfiguration"/> carrying the planted key must equal the one built
    /// without it. That demonstrates the key genuinely was in play and made no difference, where
    /// asserting only its absence from the output would pass just as happily for a key that never
    /// reached the type.
    /// </remarks>
    [Fact]
    public async Task The_api_key_is_not_part_of_the_fingerprint()
    {
        const string plantedKey = "planted-fingerprint-key-0123456789";

        var withoutKey = new ResolvedSourceConfiguration();
        withoutKey.Apply("http://192.0.2.50:5076/", apiKey: null, sourceName: "NZBHydra2");

        var withKey = new ResolvedSourceConfiguration();
        withKey.Apply("http://192.0.2.50:5076/", plantedKey, sourceName: "NZBHydra2");

        var fromWithout = await new ResolvedSourceSetFingerprintSource(withoutKey)
            .GetAsync(CancellationToken.None);
        var fromWith = await new ResolvedSourceSetFingerprintSource(withKey)
            .GetAsync(CancellationToken.None);

        Assert.DoesNotContain(plantedKey, fromWith, StringComparison.Ordinal);
        Assert.Equal(fromWithout, fromWith);
    }

    /// <summary>
    /// A different source set DOES produce a different fingerprint — the property the whole change
    /// rests on. Without this the assertions above would all pass for a constant.
    /// </summary>
    [Fact]
    public async Task A_different_source_set_produces_a_different_fingerprint()
    {
        var first = new ResolvedSourceConfiguration();
        first.Apply("http://192.0.2.50:5076/", apiKey: null, sourceName: "NZBHydra2");

        var relocated = new ResolvedSourceConfiguration();
        relocated.Apply("http://192.0.2.51:5076/", apiKey: null, sourceName: "NZBHydra2");

        var unconfigured = new ResolvedSourceConfiguration();

        var firstFingerprint = await new ResolvedSourceSetFingerprintSource(first).GetAsync(CancellationToken.None);
        var relocatedFingerprint = await new ResolvedSourceSetFingerprintSource(relocated).GetAsync(CancellationToken.None);
        var unconfiguredFingerprint = await new ResolvedSourceSetFingerprintSource(unconfigured).GetAsync(CancellationToken.None);

        Assert.NotEqual(firstFingerprint, relocatedFingerprint);

        // First-time configuration is the case the issue is actually about, and it is caught by the
        // base URL rather than by the key: an unconfigured instance resolves no URL at all.
        Assert.NotEqual(unconfiguredFingerprint, firstFingerprint);
    }
}
