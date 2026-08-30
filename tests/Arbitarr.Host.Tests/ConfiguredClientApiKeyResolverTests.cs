using System.Runtime.CompilerServices;
using Arbitarr.Host.Security;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// Exercises <see cref="ConfiguredClientApiKeyResolver"/>: fail-closed with zero configured keys,
/// named-key resolution (including the legacy single-<c>Arbitarr:ApiKey</c> collapse to a
/// <c>"default"</c> named key, mirrored from <c>Program.cs</c>), wrong/empty/whitespace key
/// denial, and a source-level guard that the comparison actually goes through
/// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/> rather than
/// a variable-time <c>==</c>/<c>SequenceEqual</c> comparison.
/// </summary>
public class ConfiguredClientApiKeyResolverTests
{
    [Fact]
    public void Resolve_WithNoConfiguredKeys_DeniesEverything()
    {
        var resolver = new ConfiguredClientApiKeyResolver(Array.Empty<NamedClientApiKey>());

        var result = resolver.Resolve("anything");

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_WithMatchingNamedKey_ReturnsMatchingContext()
    {
        var resolver = new ConfiguredClientApiKeyResolver(new[]
        {
            new NamedClientApiKey("sonarr", "sonarr-key-value"),
            new NamedClientApiKey("radarr", "radarr-key-value"),
        });

        var result = resolver.Resolve("radarr-key-value");

        Assert.NotNull(result);
        Assert.Equal("radarr", result!.Name);
    }

    [Fact]
    public void Resolve_WithLegacySingleApiKeyCollapsedToDefault_MatchesDefaultKey()
    {
        // Mirrors Program.cs: a single legacy Arbitarr:ApiKey value (no name) collapses to one
        // named key, "default".
        const string legacyKey = "legacy-secret-api-key";
        var keys = new[] { new NamedClientApiKey("default", legacyKey) };
        var resolver = new ConfiguredClientApiKeyResolver(keys);

        var result = resolver.Resolve(legacyKey);

        Assert.NotNull(result);
        Assert.Equal("default", result!.Name);
    }

    [Fact]
    public void Resolve_WithWrongKey_ReturnsNull()
    {
        var resolver = new ConfiguredClientApiKeyResolver(new[]
        {
            new NamedClientApiKey("default", "correct-key"),
        });

        var result = resolver.Resolve("wrong-key");

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_WithEmptyPresentedKey_ReturnsNull()
    {
        var resolver = new ConfiguredClientApiKeyResolver(new[]
        {
            new NamedClientApiKey("default", "correct-key"),
        });

        var result = resolver.Resolve(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_WithWhitespaceOnlyPresentedKey_ReturnsNull()
    {
        // string.IsNullOrEmpty does not catch whitespace-only input, so this must fall through to
        // (and fail) the fixed-time comparison loop rather than the early null/empty guard.
        var resolver = new ConfiguredClientApiKeyResolver(new[]
        {
            new NamedClientApiKey("default", "correct-key"),
        });

        var result = resolver.Resolve("   ");

        Assert.Null(result);
    }

    /// <summary>
    /// Non-vacuous guard: greps the resolver's own source file for the fixed-time comparison call,
    /// so this test fails if a future edit swaps it for a variable-time comparison (<c>==</c>,
    /// <c>SequenceEqual</c>, etc.) even though such a change wouldn't be caught by the
    /// behavioral tests above (which can't observe timing).
    /// </summary>
    [Fact]
    public void Resolve_ComparisonImplementation_UsesFixedTimeEquals()
    {
        var sourcePath = ResolveSourcePath();

        var source = File.ReadAllText(sourcePath);

        Assert.Contains("CryptographicOperations.FixedTimeEquals", source);
    }

    private static string ResolveSourcePath([CallerFilePath] string testFilePath = "")
    {
        // testFilePath: .../tests/Arbitarr.Host.Tests/ConfiguredClientApiKeyResolverTests.cs
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(testFilePath))!; // .../tests
        var repoRoot = Path.GetDirectoryName(testsDir)!; // repo root
        var sourcePath = Path.Combine(repoRoot, "src", "Arbitarr.Host", "Security", "ConfiguredClientApiKeyResolver.cs");

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Expected to find ConfiguredClientApiKeyResolver.cs at '{sourcePath}'.", sourcePath);
        }

        return sourcePath;
    }
}
