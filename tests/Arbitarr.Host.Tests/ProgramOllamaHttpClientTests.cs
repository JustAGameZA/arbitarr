using System.Runtime.CompilerServices;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// M5 security review (HIGH): <c>Program.cs</c>'s Ollama <c>AddHttpClient</c> registration must
/// disable automatic redirect-following (SEC-M5, mirroring the NZBHydra SEC-M1 registration) —
/// defense in depth against a compromised/misconfigured Ollama endpoint 30x-ing the app to an
/// arbitrary host before any origin check could see the real target.
///
/// <para>
/// Program.cs's top-level statements aren't independently constructible/unit-testable without
/// booting the full host, so — mirroring
/// <see cref="ConfiguredClientApiKeyResolverTests.Resolve_ComparisonImplementation_UsesFixedTimeEquals"/> —
/// this is a non-vacuous source-level guard: it fails if a future edit removes the
/// <c>AllowAutoRedirect = false</c> handler configuration from the Ollama client registration,
/// even though that removal wouldn't be caught by any behavioral test.
/// </para>
/// </summary>
public class ProgramOllamaHttpClientTests
{
    [Fact]
    public void OllamaHttpClientRegistration_DisablesAutoRedirect()
    {
        var source = File.ReadAllText(ResolveProgramCsPath());

        var ollamaRegistrationStart = source.IndexOf("nameof(OllamaClient)", StringComparison.Ordinal);
        Assert.True(ollamaRegistrationStart >= 0, "Expected to find the Ollama AddHttpClient registration in Program.cs.");

        // Look at the statement immediately following the registration call (its fluent chain),
        // not the whole file, so this test fails if AllowAutoRedirect=false is removed from THIS
        // registration specifically, rather than merely existing somewhere else in the file.
        var window = source.Substring(ollamaRegistrationStart, Math.Min(400, source.Length - ollamaRegistrationStart));

        Assert.Contains("ConfigurePrimaryHttpMessageHandler", window, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", window, StringComparison.Ordinal);
    }

    private static string ResolveProgramCsPath([CallerFilePath] string testFilePath = "")
    {
        // testFilePath: .../tests/Arbitarr.Host.Tests/ProgramOllamaHttpClientTests.cs
        var testsDir = Path.GetDirectoryName(Path.GetDirectoryName(testFilePath))!; // .../tests
        var repoRoot = Path.GetDirectoryName(testsDir)!; // repo root
        var programCsPath = Path.Combine(repoRoot, "src", "Arbitarr.Host", "Program.cs");

        if (!File.Exists(programCsPath))
        {
            throw new FileNotFoundException($"Expected to find Program.cs at '{programCsPath}'.", programCsPath);
        }

        return programCsPath;
    }
}
