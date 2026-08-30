using System.Runtime.CompilerServices;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// <see cref="Arbitarr.Ai.ClassifierWorker.ClassifyAndCacheAsync"/> has no caller in the running
/// Host unless something drives it. <c>Program.cs</c> must register <c>ClassifierPollingWorker</c> — the
/// wrapping <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that polls
/// <c>InMemoryReleaseLookup</c> and drives classification — as a hosted service.
///
/// <para>
/// Program.cs's top-level statements aren't independently constructible/unit-testable without
/// booting the full host, so — mirroring <see cref="ProgramOllamaHttpClientTests"/> — this is a
/// non-vacuous source-level guard: it fails if a future edit removes the
/// <c>AddHostedService</c> registration for <c>ClassifierPollingWorker</c> from Program.cs.
/// </para>
/// </summary>
public class ProgramClassifierPollingWorkerTests
{
    [Fact]
    public void ClassifierPollingWorker_IsRegisteredAsHostedService()
    {
        var source = File.ReadAllText(ResolveProgramCsPath());

        var registrationStart = source.IndexOf("new ClassifierPollingWorker(", StringComparison.Ordinal);
        Assert.True(registrationStart >= 0, "Expected to find a ClassifierPollingWorker construction in Program.cs.");

        // Look backwards a short window to confirm this construction is inside an AddHostedService
        // call, not merely constructed and discarded/used some other way.
        var precedingWindowStart = Math.Max(0, registrationStart - 200);
        var precedingWindow = source.Substring(precedingWindowStart, registrationStart - precedingWindowStart);

        Assert.Contains("AddHostedService", precedingWindow, StringComparison.Ordinal);
    }

    private static string ResolveProgramCsPath([CallerFilePath] string testFilePath = "")
    {
        // testFilePath: .../tests/Arbitarr.Host.Tests/ProgramClassifierPollingWorkerTests.cs
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
