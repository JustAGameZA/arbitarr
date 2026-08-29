using System.Reflection;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// M0: the solution was renamed from the working name "ArrSearcher" to "Arbitarr". This test
/// guards against the rename being silently reverted or partially undone in a future change -
/// no assembly built alongside the test binaries may carry the old "ArrSearcher" prefix.
/// </summary>
public class AssemblyNamingTests
{
    [Fact]
    public void No_Loaded_Solution_Assembly_Starts_With_ArrSearcher()
    {
        // Mirrors the loading mechanism used by the other tests in this project: any
        // Arbitarr.* assembly copied alongside the test binaries (via direct or transitive
        // ProjectReference) is loaded by path and inspected by name, independent of any
        // specific type it exposes.
        var arbitarrDllPaths = Directory.GetFiles(AppContext.BaseDirectory, "Arbitarr.*.dll");

        Assert.True(arbitarrDllPaths.Length > 0, $"Expected to find Arbitarr.*.dll files in {AppContext.BaseDirectory}.");

        var loadedNames = AppDomain.CurrentDomain
            .GetAssemblies()
            .Concat(arbitarrDllPaths.Select(Assembly.LoadFrom))
            .Select(a => a.GetName().Name)
            .Where(n => n is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var violations = loadedNames
            .Where(n => n.StartsWith("ArrSearcher", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "No loaded solution assembly may start with the old working name 'ArrSearcher', " +
            $"but found: {string.Join(", ", violations)}");
    }
}
