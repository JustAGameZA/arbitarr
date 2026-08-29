using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace ArrSearcher.Architecture.Tests;

/// <summary>
/// AC6a: ArrSearcher.Ai and ArrSearcher.Media are sibling domain projects that must never reference
/// each other in either direction (per the plan's Step 1 dependency tree). Compliance is already
/// achieved in the .csproj files themselves, but until this test existed nothing enforced it - a
/// future change adding either reference would compile cleanly and go unnoticed.
/// </summary>
public class AiMediaIsolationTests
{
    private static readonly Assembly AiAssembly = AppDomain.CurrentDomain
        .GetAssemblies()
        .Concat(new[] { Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "ArrSearcher.Ai.dll")) })
        .First(a => a.GetName().Name == "ArrSearcher.Ai");

    private static readonly Assembly MediaAssembly = AppDomain.CurrentDomain
        .GetAssemblies()
        .Concat(new[] { Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "ArrSearcher.Media.dll")) })
        .First(a => a.GetName().Name == "ArrSearcher.Media");

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
            !referencedNames.Contains("ArrSearcher.Media", StringComparer.Ordinal),
            "ArrSearcher.Ai must not reference ArrSearcher.Media, but referenced: " +
            string.Join(", ", referencedNames.Where(n => n.StartsWith("ArrSearcher.", StringComparison.Ordinal))));

        var result = Types.InAssembly(AiAssembly)
            .Should()
            .NotHaveDependencyOnAny("ArrSearcher.Media")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "ArrSearcher.Ai has a forbidden dependency on ArrSearcher.Media: " +
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
            !referencedNames.Contains("ArrSearcher.Ai", StringComparer.Ordinal),
            "ArrSearcher.Media must not reference ArrSearcher.Ai, but referenced: " +
            string.Join(", ", referencedNames.Where(n => n.StartsWith("ArrSearcher.", StringComparison.Ordinal))));

        var result = Types.InAssembly(MediaAssembly)
            .Should()
            .NotHaveDependencyOnAny("ArrSearcher.Ai")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "ArrSearcher.Media has a forbidden dependency on ArrSearcher.Ai: " +
            string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>()));
    }
}
