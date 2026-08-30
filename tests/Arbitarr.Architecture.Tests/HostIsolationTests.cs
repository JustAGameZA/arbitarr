using System.Reflection;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// AC6: Arbitarr.Host is the sole composition root permitted to reference source-adapter
/// projects (Arbitarr.Sources.*). No other Arbitarr.* project may reference any
/// Arbitarr.Sources.* assembly, directly or otherwise. This must fail when a deliberate
/// illegal reference is introduced (verified manually per the plan's Step 1 acceptance
/// criteria — see the M1 handoff notes), not merely pass on a vacuous/empty check.
/// </summary>
public class HostIsolationTests
{
    // All non-Host Arbitarr.* assemblies that are expected to be present alongside the test
    // binaries via ProjectReference (direct or transitive). Arbitarr.Host itself is excluded:
    // it is the one project explicitly allowed to reference Arbitarr.Sources.*, and it uses
    // Sdk="Microsoft.NET.Sdk.Web" so it is not referenced here as a ProjectReference.
    private static readonly string[] NonHostAssemblyNames =
    {
        "Arbitarr.Core",
        "Arbitarr.Core.Identity",
        "Arbitarr.Ai",
        "Arbitarr.Media",
        "Arbitarr.Data",
        "Arbitarr.Api",
        "Arbitarr.Sources.NzbHydra",
    };

    [Fact]
    public void No_NonHost_Project_References_Any_Arbitarr_Sources_Assembly()
    {
        var violations = new List<string>();

        foreach (var name in NonHostAssemblyNames)
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, $"{name}.dll");
            Assert.True(File.Exists(dllPath), $"Expected to find {dllPath} alongside the test binaries.");

            var assembly = Assembly.LoadFrom(dllPath);

            // Arbitarr.Sources.NzbHydra referencing itself is not a violation; every other
            // Arbitarr.Sources.* reference (including NzbHydra referencing a *different*
            // source adapter) would be.
            var sourceReferences = assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .Where(n => n is not null)
                .Cast<string>()
                .Where(n => n.StartsWith("Arbitarr.Sources.", StringComparison.Ordinal) && n != name)
                .ToArray();

            if (sourceReferences.Length > 0)
            {
                violations.Add($"{name} -> {string.Join(", ", sourceReferences)}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Only Arbitarr.Host may reference Arbitarr.Sources.* assemblies, but found: " +
            string.Join("; ", violations));
    }
}
