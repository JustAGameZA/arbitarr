using System.Reflection;
using Xunit;

namespace ArrSearcher.Architecture.Tests;

/// <summary>
/// AC6: ArrSearcher.Core sits at the bottom of the dependency graph (per the plan's Step 1
/// tree) and must have zero references to any other ArrSearcher.* project.
/// </summary>
public class CoreIsolationTests
{
    [Fact]
    public void Core_Has_Zero_References_To_Any_Other_ArrSearcher_Project()
    {
        // Load the ArrSearcher.Core assembly by path (it is copied alongside the test binaries
        // via the transitive ProjectReference through ArrSearcher.Core.Identity) to inspect its
        // own references directly. Loaded by path/name rather than typeof(...) to keep this test
        // independent of any specific type ArrSearcher.Core happens to expose.
        var coreDllPath = Path.Combine(
            AppContext.BaseDirectory,
            "ArrSearcher.Core.dll");

        Assert.True(File.Exists(coreDllPath), $"Expected to find {coreDllPath} alongside the test binaries.");

        var loaded = Assembly.LoadFrom(coreDllPath);

        var referencedArrSearcherProjects = loaded
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Cast<string>()
            .Where(n => n.StartsWith("ArrSearcher.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            referencedArrSearcherProjects.Length == 0,
            "ArrSearcher.Core must have zero references to other ArrSearcher.* projects, " +
            $"but referenced: {string.Join(", ", referencedArrSearcherProjects)}");
    }
}
