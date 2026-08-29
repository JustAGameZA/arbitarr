using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// AC6 / AC6a: enforces the plan's dependency-direction guarantees for the domain projects
/// that exist at Step 1. These tests must fail when a deliberate illegal reference is
/// introduced, not merely pass on an empty/vacuous check — see the plan's Step 1 acceptance
/// criteria and the team handoff's explicit call-out of this risk.
/// </summary>
public class DependencyDirectionTests
{
    // Arbitarr.Core.Identity is the assembly under test for the "no downstream references"
    // rule (AC6a's shape: a low-level project must not reach into higher-level projects).
    // Resolved by assembly name (rather than typeof(...)) to keep this test independent of any
    // specific type Arbitarr.Core.Identity happens to expose.
    private static readonly Assembly CoreIdentityAssembly = AppDomain.CurrentDomain
        .GetAssemblies()
        .Concat(new[] { Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Arbitarr.Core.Identity.dll")) })
        .First(a => a.GetName().Name == "Arbitarr.Core.Identity");

    [Fact]
    public void CoreIdentity_Does_Not_Reference_Api_Ai_Or_Media()
    {
        var forbidden = new[] { "Arbitarr.Api", "Arbitarr.Ai", "Arbitarr.Media" };

        var referencedNames = CoreIdentityAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Cast<string>()
            .ToArray();

        var violations = referencedNames
            .Where(n => forbidden.Any(f => n.Equals(f, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Arbitarr.Core.Identity must not reference {string.Join(", ", forbidden)}, " +
            $"but referenced: {string.Join(", ", violations)}");
    }

    [Fact]
    public void CoreIdentity_Types_Do_Not_Reside_In_Forbidden_Namespaces()
    {
        // Belt-and-braces NetArchTest.Rules check over the compiled assembly: no type in
        // Arbitarr.Core.Identity may live under (or depend on being resolved alongside)
        // the Api/Ai/Media namespaces.
        var result = Types.InAssembly(CoreIdentityAssembly)
            .Should()
            .NotHaveDependencyOnAny("Arbitarr.Api", "Arbitarr.Ai", "Arbitarr.Media")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Arbitarr.Core.Identity has a forbidden dependency on Api/Ai/Media: " +
            string.Join(", ", result.FailingTypeNames ?? Enumerable.Empty<string>()));
    }
}
