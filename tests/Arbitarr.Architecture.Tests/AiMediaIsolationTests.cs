using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// AC6a: Arbitarr.Ai and Arbitarr.Media are sibling domain projects that must never reference
/// each other in either direction (per the plan's Step 1 dependency tree). Compliance is already
/// achieved in the .csproj files themselves, but until this test existed nothing enforced it - a
/// future change adding either reference would compile cleanly and go unnoticed.
/// </summary>
public class AiMediaIsolationTests
{
    private static readonly Assembly AiAssembly = AppDomain.CurrentDomain
        .GetAssemblies()
        .Concat(new[] { Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Arbitarr.Ai.dll")) })
        .First(a => a.GetName().Name == "Arbitarr.Ai");

    private static readonly Assembly MediaAssembly = AppDomain.CurrentDomain
        .GetAssemblies()
        .Concat(new[] { Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Arbitarr.Media.dll")) })
        .First(a => a.GetName().Name == "Arbitarr.Media");

    [Fact]
    public void Ai_Does_Not_Reference_Media()
    {
        var referencedNames = AiAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Cast<string>()
            .ToArray();

        Assert.True(
            !referencedNames.Contains("Arbitarr.Media", StringComparer.Ordinal),
            "Arbitarr.Ai must not reference Arbitarr.Media, but referenced: " +
            string.Join(", ", referencedNames.Where(n => n.StartsWith("Arbitarr.", StringComparison.Ordinal))));

        var result = Types.InAssembly(AiAssembly)
            .Should()
            .NotHaveDependencyOnAny("Arbitarr.Media")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Arbitarr.Ai has a forbidden dependency on Arbitarr.Media: " +
            string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>()));
    }

    [Fact]
    public void Media_Does_Not_Reference_Ai()
    {
        var referencedNames = MediaAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Cast<string>()
            .ToArray();

        Assert.True(
            !referencedNames.Contains("Arbitarr.Ai", StringComparer.Ordinal),
            "Arbitarr.Media must not reference Arbitarr.Ai, but referenced: " +
            string.Join(", ", referencedNames.Where(n => n.StartsWith("Arbitarr.", StringComparison.Ordinal))));

        var result = Types.InAssembly(MediaAssembly)
            .Should()
            .NotHaveDependencyOnAny("Arbitarr.Ai")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Arbitarr.Media has a forbidden dependency on Arbitarr.Ai: " +
            string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>()));
    }
}
