using System.Reflection;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// AC6: Arbitarr.Core sits at the bottom of the dependency graph (per the plan's Step 1
/// tree) and must have zero references to any other Arbitarr.* project.
/// </summary>
public class CoreIsolationTests
{
    [Fact]
    public void Core_Has_Zero_References_To_Any_Other_Arbitarr_Project()
    {
        // Load the Arbitarr.Core assembly by path (it is copied alongside the test binaries
        // via the transitive ProjectReference through Arbitarr.Core.Identity) to inspect its
        // own references directly. Loaded by path/name rather than typeof(...) to keep this test
        // independent of any specific type Arbitarr.Core happens to expose.
        var coreDllPath = Path.Combine(
            AppContext.BaseDirectory,
            "Arbitarr.Core.dll");

        Assert.True(File.Exists(coreDllPath), $"Expected to find {coreDllPath} alongside the test binaries.");

        var loaded = Assembly.LoadFrom(coreDllPath);

        var referencedArbitarrProjects = loaded
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Cast<string>()
            .Where(n => n.StartsWith("Arbitarr.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            referencedArbitarrProjects.Length == 0,
            "Arbitarr.Core must have zero references to other Arbitarr.* projects, " +
            $"but referenced: {string.Join(", ", referencedArbitarrProjects)}");
    }
}
