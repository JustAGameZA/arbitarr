using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-pujk: <c>Arbitarr:ReleaseGuidSecret</c> is honoured from the full configuration chain, and
/// before this it was decoded with an unguarded <c>Convert.FromBase64String</c> and handed straight
/// to <see cref="Arbitarr.Api.Rendering.ReleaseGuid.Configure"/>, which rejects only an EMPTY key.
/// So a one-byte value such as a two-character base64 string was accepted as the HMAC-SHA256 key
/// and every proxy link became guessable, with no exception and no log line; a value that was not
/// base64 at all failed startup with a bare <c>FormatException</c> naming neither the key nor the
/// requirement. Program.cs now validates both before use.
///
/// <para><b>Why this is a host test.</b> The validation lives in the composition root's top-level
/// statements, after <c>builder.Build()</c>. Only starting a real host through the same
/// configuration chain an operator would use exercises it; a unit test would have to restate the
/// rule rather than run it.</para>
///
/// <para><b>Positive control (CLAUDE.md §4).</b> "The message does not contain the supplied value"
/// passes just as happily when no message was produced at all, or when the value was never in play.
/// Each failing case therefore asserts FIRST that the exception message carries the sentinel
/// substrings that prove validation ran and produced the intended message — the key name and the
/// "32 bytes" requirement — and only THEN that the planted value is absent from it. The planted
/// values are chosen to be distinctive strings that could not occur incidentally, so their absence
/// is evidence rather than coincidence.</para>
///
/// <para><b>Collection.</b> This assembly runs its classes in parallel (see AssemblyInfo.cs) and
/// <c>ReleaseGuid</c>'s secret is a process-global. The two failing cases never reach
/// <c>ReleaseGuid.Configure</c> at all, so they write no global; the passing control writes one
/// exactly as any other host build does, and supplies its own secret rather than letting one be
/// generated, so it is no more perturbing than <see cref="ArbitarrWebApplicationFactory"/>. This
/// class is nevertheless serialised against itself in its own collection so that its three hosts do
/// not start concurrently. It deliberately does NOT join
/// <c>ReleaseGuidSecretSwapDuringRequestTests</c>'s collection: that class's doc comment reserves
/// it, because it swaps the global mid-request and nothing else may run beside it.</para>
/// </summary>
[Collection(ReleaseGuidSecretValidationTests.CollectionName)]
public sealed class ReleaseGuidSecretValidationTests : IDisposable
{
    internal const string CollectionName = "ReleaseGuidSecretValidation(Integration)";

    /// <summary>
    /// Not base64: the padding character sits mid-string, which no decoder accepts. Distinctive
    /// enough that finding it in an exception message could only mean the message echoed it.
    /// </summary>
    private const string MalformedValue = "not=base64=arb-pujk";

    /// <summary>
    /// Valid base64 that decodes to exactly 16 bytes — half the requirement. This is the case that
    /// mattered: it decoded cleanly, so no decoder ever complained, and only a length check catches
    /// it. The plaintext is a readable marker rather than key-shaped, so it is not a credential and
    /// the pre-commit secret guard has nothing to flag.
    /// </summary>
    private const string SixteenByteValue = "YXJiLXB1amstMTZieXRlcw==";

    /// <summary>Valid base64 decoding to exactly 32 bytes: the supported shape, and the control.</summary>
    private const string ThirtyTwoByteValue = "YXJiLXB1amstc2VjcmV0LWV4YWN0bHktMzJieXRlcyE=";

    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(), "arbitarr-guid-secret-validation-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebApplicationFactory<Program>> _factories = new();

    public ReleaseGuidSecretValidationTests() => Directory.CreateDirectory(_configDirectory);

    private WebApplicationFactory<Program> CreateHost(string releaseGuidSecret)
    {
        // UseSetting, never an environment variable, for the reason ArbitarrWebApplicationFactory
        // gives: the env var is process-wide and this assembly runs classes in parallel.
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);
            builder.UseSetting("Arbitarr:ReleaseGuidSecret", releaseGuidSecret);
        });
        _factories.Add(factory);
        return factory;
    }

    [Fact]
    public void HostStart_WithMalformedBase64Secret_FailsWithNamedMessageCarryingNoKeyMaterial()
    {
        var host = CreateHost(MalformedValue);

        // CreateClient() is what forces host startup, so the composition root's validation runs here.
        var exception = Record.Exception(() => host.CreateClient());

        Assert.NotNull(exception);
        var message = Flatten(exception);

        // Positive control FIRST: prove validation ran and produced the intended message. Without
        // these, the absence assertion below would pass on any failure whatsoever — or on none.
        Assert.Contains("Arbitarr:ReleaseGuidSecret", message, StringComparison.Ordinal);
        Assert.Contains("32 bytes", message, StringComparison.Ordinal);
        Assert.Contains("base64", message, StringComparison.Ordinal);

        // Only now is the absence assertion meaningful: the message exists, is the right message,
        // and does not echo what the operator configured.
        Assert.DoesNotContain(MalformedValue, message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostStart_WithSixteenByteSecret_FailsWithNamedMessageCarryingNoKeyMaterial()
    {
        // The silent case: this decodes perfectly. Pre-fix the host started and served guessable
        // links. Assert the decode really does yield 16 bytes, so the test cannot drift into
        // exercising the malformed path instead and still pass.
        Assert.Equal(16, Convert.FromBase64String(SixteenByteValue).Length);

        var host = CreateHost(SixteenByteValue);

        var exception = Record.Exception(() => host.CreateClient());

        Assert.NotNull(exception);
        var message = Flatten(exception);

        Assert.Contains("Arbitarr:ReleaseGuidSecret", message, StringComparison.Ordinal);
        Assert.Contains("32 bytes", message, StringComparison.Ordinal);

        Assert.DoesNotContain(SixteenByteValue, message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostStart_WithThirtyTwoByteSecret_StartsNormally()
    {
        // The control. Without it, the two assertions above are satisfied by a host that refuses to
        // start for any reason at all, and the validation could be rejecting everything.
        Assert.Equal(32, Convert.FromBase64String(ThirtyTwoByteValue).Length);

        var host = CreateHost(ThirtyTwoByteValue);

        using var client = host.CreateClient();

        Assert.NotNull(client);
    }

    /// <summary>
    /// The whole exception chain's text. The host wraps startup failures, so asserting only on
    /// <c>exception.Message</c> would both miss the message and — worse for the absence assertion —
    /// fail to see a value echoed by an inner exception.
    /// </summary>
    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(Environment.NewLine, parts);
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not a test failure.
        }
    }
}
