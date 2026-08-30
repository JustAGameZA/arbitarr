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

    // M6-7: walks the full transitive reference graph starting from Arbitarr.Core.Identity,
    // rather than only its direct references, so a future indirect path (Core.Identity -> X ->
    // Arbitarr.Media) would be caught even though X itself is not one of the forbidden names.
    private static HashSet<string> GetTransitiveReferenceClosure(Assembly root)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var reference in current.GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name is null || !visited.Add(name))
                {
                    continue;
                }

                // Only walk further into assemblies we can actually load (i.e. our own
                // Arbitarr.* projects); framework/BCL/NuGet assemblies are leaves for this check.
                if (!name.StartsWith("Arbitarr.", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var loaded = Assembly.Load(reference);
                    queue.Enqueue(loaded);
                }
                catch (Exception) when (true)
                {
                    // Unresolvable transitive reference: nothing further to walk from here, but
                    // its name is still recorded above so the closure remains accurate.
                }
            }
        }

        return visited;
    }

    [Fact]
    public void CoreIdentity_TransitiveClosure_Does_Not_Reach_Media()
    {
        var closure = GetTransitiveReferenceClosure(CoreIdentityAssembly);

        // Non-vacuousness: prove the walk actually ran and enumerated real references (and isn't
        // silently empty because nothing loaded). Note Core.Identity's compiled IL carries no
        // Arbitarr.Core reference despite the ProjectReference in its .csproj: the C# compiler
        // elides an assembly reference when no emitted type actually uses it, and no
        // Arbitarr.Core.Identity source file currently references an Arbitarr.Core type. The
        // walk's ability to traverse multiple real Arbitarr.* hops is instead proven by
        // TransitiveClosureHelper_WalksMultipleHops_FromMedia below.
        Assert.NotEmpty(closure);

        Assert.DoesNotContain("Arbitarr.Media", closure);
    }

    // Positive control for CoreIdentity_TransitiveClosure_Does_Not_Reach_Media: proves the walk
    // itself performs real multi-hop traversal (rather than the negative result above being
    // explainable by a walk that silently does nothing) by starting from Arbitarr.Media — a real,
    // already-loaded assembly — and confirming the closure reaches both of its known transitive
    // project dependencies, Arbitarr.Core and Arbitarr.Core.Identity (per Media's own
    // ProjectReferences). If the walk were broken/no-op, this closure would come back empty and
    // this test would fail exactly where the Media-detection assertion above would have.
    [Fact]
    public void TransitiveClosureHelper_WalksMultipleHops_FromMedia()
    {
        var mediaAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .Concat(new[] { Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Arbitarr.Media.dll")) })
            .First(a => a.GetName().Name == "Arbitarr.Media");

        var closure = GetTransitiveReferenceClosure(mediaAssembly);

        Assert.Contains("Arbitarr.Core", closure);
        Assert.Contains("Arbitarr.Core.Identity", closure);
    }
}
