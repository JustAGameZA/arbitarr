using Arbitarr.Api.Search;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Microsoft.EntityFrameworkCore;
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
///
/// <para><b>arb-x7w8.4 moved the DERIVATION tests out of this file</b>, to
/// <c>Arbitarr.Host.Tests.ResolvedSourceSetFingerprintSourceTests</c>. The fingerprint is now read
/// from the <c>Sources</c> table per call rather than hashed once from a
/// <see cref="ResolvedSourceConfiguration"/>, so "what goes into it" is a question about rows and
/// belongs beside the other row-level Host tests. What stays here is what only a real container can
/// answer: that the registration exists, points at the live implementation, and produces a
/// non-inert value.</para>
/// </remarks>
public sealed class SourceSetFingerprintWiringTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public SourceSetFingerprintWiringTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The contract resolves, and resolves to the implementation that reads the enabled source rows.
    /// Asserting the concrete type matters: a registration pointing at
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
    /// The fingerprint is stable across scopes for an UNCHANGED source set. A value that varied per
    /// request would give every request its own snapshot token — silently disabling the pagination
    /// snapshot entirely, which is a far worse regression than the staleness the mechanism fixes.
    /// </summary>
    /// <remarks>
    /// Note what arb-x7w8.4 did and did NOT change here. The fingerprint is now derived per call
    /// rather than once at construction, so it CAN differ between two scopes — but only when the
    /// rows differ. Same rows, same value; that is the property this asserts, and
    /// <c>A_source_added_between_scopes_changes_the_fingerprint</c> below asserts the other half.
    /// </remarks>
    [Fact]
    public async Task The_fingerprint_is_stable_across_scopes_for_an_unchanged_source_set()
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
    /// arb-x7w8.4's actual property, asserted through the REAL container: a source added while the
    /// process is running changes the fingerprint on the next scope.
    /// </summary>
    /// <remarks>
    /// <para>This is the one the whole change rests on, and it is why the derivation could not stay
    /// frozen at startup. Before the source registry, a source set could not change without a
    /// restart, so hashing once was sound. Now it can — and a fingerprint that did not move would
    /// hand this host's persisted snapshot rows, produced by the OLD source set, to searches run
    /// against the NEW one.</para>
    ///
    /// <para>It owns its own factory rather than sharing the class fixture because it WRITES a row:
    /// the shared fixture is injected into this class's other tests, whose stability assertions
    /// would then be asserting over a set this test had mutated underneath them.</para>
    /// </remarks>
    [Fact]
    public async Task A_source_added_between_scopes_changes_the_fingerprint()
    {
        await using var factory = new ArbitarrWebApplicationFactory();

        string Fingerprint()
        {
            using var scope = factory.Services.CreateScope();
            return scope.ServiceProvider
                .GetRequiredService<ISourceSetFingerprintSource>()
                .GetAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        var before = Fingerprint();

        await factory.SeedAsync(db => db.Sources.AddAsync(new Source
        {
            Kind = SourceRepository.NewznabKind,
            DisplayName = "fingerprint-runtime-addition",
            BaseUrl = "http://192.0.2.80:9117",
            ApiPath = "/api",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).AsTask());

        var after = Fingerprint();

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// Disabling a source changes the fingerprint too — the same property from the other direction,
    /// and the one a "read every row" implementation would get wrong while passing the test above.
    /// </summary>
    [Fact]
    public async Task Disabling_a_source_changes_the_fingerprint()
    {
        await using var factory = new ArbitarrWebApplicationFactory();

        string Fingerprint()
        {
            using var scope = factory.Services.CreateScope();
            return scope.ServiceProvider
                .GetRequiredService<ISourceSetFingerprintSource>()
                .GetAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        await factory.SeedAsync(db => db.Sources.AddAsync(new Source
        {
            Kind = SourceRepository.TorznabKind,
            DisplayName = "fingerprint-disable-subject",
            BaseUrl = "http://192.0.2.81:9117",
            ApiPath = "/api",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).AsTask());

        var whileEnabled = Fingerprint();

        await factory.SeedAsync(async db =>
        {
            var row = await db.Sources.FirstAsync(s => s.DisplayName == "fingerprint-disable-subject");
            row.Enabled = false;
        });

        Assert.NotEqual(whileEnabled, Fingerprint());
    }
}
